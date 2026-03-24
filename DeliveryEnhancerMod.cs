using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using ScheduleOne.Configuration;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using ScheduleOne.Networking;
using ScheduleOne.UI.Phone.Delivery;
using ScheduleOne.UI.Shop;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Schedule1DeliveryEnhancer.DeliveryEnhancerMod), "Schedule1 Delivery Enhancer", "0.2.0", "Guy")]
[assembly: MelonGame(null, "Schedule 1")]

namespace Schedule1DeliveryEnhancer
{
    public sealed class DeliveryEnhancerMod : MelonMod
    {
        private static readonly string SaveFilePath = Path.Combine(GetUserDataDirectory(), "Schedule1DeliveryEnhancer", "delivery-data.json");
        public static DeliveryEnhancerMod Instance { get; private set; }

        private readonly List<DeliveryRecord> _history = new List<DeliveryRecord>();
        private readonly HashSet<string> _favoriteSignatures = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<RecurringDelivery> _recurring = new List<RecurringDelivery>();

        private MelonPreferences_Entry<bool> _enableRecurring;
        private DateTime _nextRecurringCheckUtc = DateTime.UtcNow;
        private int _uiRevision;

        private static string GetUserDataDirectory()
        {
            try
            {
                var melonAsm = typeof(MelonMod).Assembly;
                var envType = melonAsm.GetType("MelonLoader.MelonEnvironment");
                var prop = envType?.GetProperty("UserDataDirectory");
                var value = prop?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
                // Ignore and fall back.
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public override void OnInitializeMelon()
        {
            Instance = this;
            var category = MelonPreferences.CreateCategory("Schedule1DeliveryEnhancer");
            _enableRecurring = category.CreateEntry("EnableRecurring", true);

            LoadData();
            HarmonyInstance.PatchAll();

            MelonLogger.Msg("Schedule1 Delivery Enhancer loaded.");
            MelonLogger.Msg("Delivery app: Favorites, Recurring, Previous sections (full item details + re-order).");
        }

        public override void OnUpdate()
        {
            if (_enableRecurring.Value)
                TickRecurringDeliveries();
        }

        public void OnReceiptRecorded(DeliveryReceipt receipt)
        {
            if (receipt == null)
                return;

            var record = BuildRecord(receipt);
            _history.Add(record);
            if (_history.Count > 250)
                _history.RemoveAt(0);

            SaveData();
            MarkUiDirty();
        }

        public bool ToggleFavoriteForDelivery(DeliveryInstance instance)
        {
            var record = BuildRecord(instance);
            if (record == null)
                return false;

            bool isFavorite;
            if (_favoriteSignatures.Contains(record.Signature))
            {
                _favoriteSignatures.Remove(record.Signature);
                isFavorite = false;
                MelonLogger.Msg("Favorite removed.");
            }
            else
            {
                _favoriteSignatures.Add(record.Signature);
                isFavorite = true;
                MelonLogger.Msg("Favorite pinned.");
            }

            SaveData();
            MarkUiDirty();
            return isFavorite;
        }

        public bool ToggleRecurringForDelivery(DeliveryInstance instance, int intervalMinutes)
        {
            var record = BuildRecord(instance);
            if (record == null)
                return false;

            var existing = _recurring.FirstOrDefault(x => string.Equals(x.TemplateSignature, record.Signature, StringComparison.Ordinal));
            bool isEnabled;
            if (existing != null)
            {
                _recurring.Remove(existing);
                isEnabled = false;
                MelonLogger.Msg("Recurring removed.");
            }
            else
            {
                _recurring.Add(new RecurringDelivery
                {
                    TemplateSignature = record.Signature,
                    IntervalMinutes = Math.Max(0, intervalMinutes),
                    NextRunUtc = DateTime.UtcNow,
                    LastSubmittedUtc = DateTime.MinValue
                });
                isEnabled = true;
                MelonLogger.Msg($"Recurring enabled every {intervalMinutes}m.");
            }

            SaveData();
            MarkUiDirty();
            return isEnabled;
        }

        public bool RebuyFromDelivery(DeliveryInstance instance)
        {
            var record = BuildRecord(instance);
            return record != null && TrySubmitOrder(record, "One-click re-buy");
        }

        public bool IsFavorite(DeliveryInstance instance)
        {
            var record = BuildRecord(instance);
            return record != null && _favoriteSignatures.Contains(record.Signature);
        }

        public bool IsRecurring(DeliveryInstance instance)
        {
            var record = BuildRecord(instance);
            if (record == null)
                return false;
            return _recurring.Any(x => string.Equals(x.TemplateSignature, record.Signature, StringComparison.Ordinal));
        }

        private void TickRecurringDeliveries()
        {
            var now = DateTime.UtcNow;
            if (now < _nextRecurringCheckUtc)
                return;

            _nextRecurringCheckUtc = now.AddSeconds(3);
            if (_recurring.Count == 0)
                return;

            var changed = false;
            foreach (var recurring in _recurring)
            {
                if (now < recurring.NextRunUtc)
                    continue;

                var template = _history.LastOrDefault(x => string.Equals(x.Signature, recurring.TemplateSignature, StringComparison.Ordinal));
                if (template == null)
                {
                    MelonLogger.Warning("Recurring template no longer found in history.");
                    recurring.NextRunUtc = now;
                    changed = true;
                    continue;
                }

                var manager = NetworkSingleton<DeliveryManager>.Instance;
                if (manager == null)
                    continue;

                var hasActive = manager.Deliveries.Any(d => d.StoreName == template.StoreName && d.DestinationCode == template.DestinationCode);
                if (hasActive)
                    continue;

                if (recurring.IntervalMinutes > 0 && recurring.LastSubmittedUtc != DateTime.MinValue)
                {
                    var wait = TimeSpan.FromMinutes(recurring.IntervalMinutes);
                    if (now - recurring.LastSubmittedUtc < wait)
                        continue;
                }

                if (TrySubmitOrder(template, "Recurring"))
                {
                    recurring.LastSubmittedUtc = now;
                    recurring.NextRunUtc = now;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveData();
                MarkUiDirty();
            }
        }

        private bool TrySubmitOrder(DeliveryRecord record, string source)
        {
            try
            {
                var manager = NetworkSingleton<DeliveryManager>.Instance;
                if (manager == null)
                {
                    MelonLogger.Warning($"{source}: DeliveryManager unavailable.");
                    return false;
                }

                var hasActive = manager.Deliveries.Any(d => d.StoreName == record.StoreName && d.DestinationCode == record.DestinationCode);
                if (hasActive)
                {
                    MelonLogger.Warning($"{source}: delivery already in progress for this destination/store.");
                    return false;
                }

                var items = record.Items.Select(x => new StringIntPair(x.ItemId, x.Quantity)).ToArray();
                if (items.Length == 0)
                {
                    MelonLogger.Warning($"{source}: no items in order template.");
                    return false;
                }

                float total = record.TotalCost > 0f ? record.TotalCost : EstimateOrderTotal(record.StoreName, items);
                if (NetworkSingleton<MoneyManager>.Instance.sync___get_value_onlineBalance() < total)
                {
                    MelonLogger.Warning($"{source}: insufficient online balance.");
                    return false;
                }

                int itemCount = items.Sum(x => x.Int);
                int travelMins = EstimateDeliveryMinutes(itemCount);
                var instance = new DeliveryInstance(GUIDManager.GenerateUniqueGUID().ToString(), record.StoreName, record.DestinationCode, record.LoadingDockIndex, items, EDeliveryStatus.InTransit, travelMins);
                manager.SendDelivery(instance);
                manager.RecordDeliveryReceipt_Server(instance.GetReceipt());
                NetworkSingleton<MoneyManager>.Instance.CreateOnlineTransaction("Delivery from " + record.StoreName, -total, 1f, string.Empty);

                MelonLogger.Msg($"{source}: submitted {record.StoreName} -> {record.DestinationCode} ({itemCount} items).");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"{source}: submit failed: {ex}");
                return false;
            }
        }

        private static int EstimateDeliveryMinutes(int itemCount)
        {
            DeliveryConfiguration config;
            if (!Singleton<ConfigurationService>.Instance.TryGetConfiguration(out config))
                return Math.Max(5, itemCount);

            int minutes = Mathf.CeilToInt(itemCount * config.Settings.DeliveryTimePerItem.Value);
            minutes = Mathf.Max(minutes, config.Settings.MinimumDeliveryTime.Value);
            minutes = Mathf.Min(minutes, config.Settings.MaximumDeliveryTime.Value);
            return minutes;
        }

        /// <summary>Used for UI price labels on live deliveries (no stored total on <see cref="DeliveryInstance"/>).</summary>
        public static float EstimateDisplayOrderTotal(string storeName, StringIntPair[] items)
        {
            return EstimateOrderTotal(storeName, items ?? Array.Empty<StringIntPair>());
        }

        private static float EstimateOrderTotal(string storeName, StringIntPair[] items)
        {
            float itemTotal = 0f;
            var shop = ShopInterface.AllShops.Find(x => x.ShopName == storeName);
            if (shop != null)
            {
                foreach (var pair in items)
                {
                    var listing = shop.Listings.Find(x => x.Item.ID == pair.String);
                    if (listing != null)
                        itemTotal += listing.Price * pair.Int;
                }
            }

            DeliveryConfiguration config;
            if (Singleton<ConfigurationService>.Instance.TryGetConfiguration(out config))
                itemTotal += config.Settings.DeliveryFee.Value;

            return itemTotal;
        }

        private DeliveryRecord BuildRecord(DeliveryReceipt receipt)
        {
            var items = new List<DeliveryLineItem>();
            if (receipt.Items != null)
            {
                foreach (var pair in receipt.Items)
                {
                    items.Add(new DeliveryLineItem { ItemId = pair.String, Quantity = pair.Int });
                }
            }

            var signature = BuildSignature(receipt.StoreName, receipt.DestinationCode, receipt.LoadingDockIndex, items);
            return new DeliveryRecord
            {
                Signature = signature,
                StoreName = receipt.StoreName,
                DestinationCode = receipt.DestinationCode,
                LoadingDockIndex = receipt.LoadingDockIndex,
                Items = items,
                TotalCost = EstimateOrderTotal(receipt.StoreName, receipt.Items ?? Array.Empty<StringIntPair>()),
                PurchasedAtUtc = DateTime.UtcNow
            };
        }

        private DeliveryRecord BuildRecord(DeliveryInstance instance)
        {
            if (instance == null)
                return null;
            return BuildRecord(instance.GetReceipt());
        }

        private static string BuildSignature(string storeName, string destinationCode, int loadingDockIndex, List<DeliveryLineItem> items)
        {
            var lines = items
                .OrderBy(x => x.ItemId, StringComparer.Ordinal)
                .ThenBy(x => x.Quantity)
                .Select(x => $"{x.ItemId}:{x.Quantity}");
            return $"{storeName}|{destinationCode}|{loadingDockIndex}|{string.Join(",", lines)}";
        }

        public int GetUiRevision() => _uiRevision;

        public void MarkUiDirty()
        {
            _uiRevision++;
        }

        public bool ToggleFavoriteBySignature(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature))
                return false;
            if (_favoriteSignatures.Contains(signature))
            {
                _favoriteSignatures.Remove(signature);
                SaveData();
                MarkUiDirty();
                return false;
            }
            _favoriteSignatures.Add(signature);
            SaveData();
            MarkUiDirty();
            return true;
        }

        public bool ToggleRecurringBySignature(string signature, int intervalMinutes)
        {
            if (string.IsNullOrWhiteSpace(signature))
                return false;
            var existing = _recurring.FirstOrDefault(x => x.TemplateSignature == signature);
            if (existing != null)
            {
                _recurring.Remove(existing);
                SaveData();
                MarkUiDirty();
                return false;
            }
            _recurring.Add(new RecurringDelivery
            {
                TemplateSignature = signature,
                IntervalMinutes = Math.Max(0, intervalMinutes),
                NextRunUtc = DateTime.UtcNow,
                LastSubmittedUtc = DateTime.MinValue
            });
            SaveData();
            MarkUiDirty();
            return true;
        }

        public bool RebuyBySignature(string signature)
        {
            var record = _history.LastOrDefault(x => x.Signature == signature);
            return record != null && TrySubmitOrder(record, "One-click re-buy");
        }

        public List<DeliveryRecord> GetTemplateRecordsLatestFirst()
        {
            return _history
                .OrderByDescending(x => x.PurchasedAtUtc)
                .GroupBy(x => x.Signature, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
        }

        public bool IsFavoriteSignature(string signature) => _favoriteSignatures.Contains(signature);
        public bool IsRecurringSignature(string signature) => _recurring.Any(x => x.TemplateSignature == signature);

        private void LoadData()
        {
            try
            {
                if (!File.Exists(SaveFilePath))
                    return;

                var raw = File.ReadAllText(SaveFilePath);
                var data = JsonConvert.DeserializeObject<PersistedData>(raw);
                if (data == null)
                    return;

                _history.Clear();
                _history.AddRange(data.History ?? new List<DeliveryRecord>());

                _favoriteSignatures.Clear();
                foreach (var sig in data.Favorites ?? new List<string>())
                    _favoriteSignatures.Add(sig);

                _recurring.Clear();
                _recurring.AddRange(data.Recurring ?? new List<RecurringDelivery>());
                MarkUiDirty();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"LoadData failed: {ex}");
            }
        }

        private void SaveData()
        {
            try
            {
                var dir = Path.GetDirectoryName(SaveFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var payload = new PersistedData
                {
                    History = _history,
                    Favorites = _favoriteSignatures.ToList(),
                    Recurring = _recurring
                };

                File.WriteAllText(SaveFilePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SaveData failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(DeliveryManager), nameof(DeliveryManager.RecordDeliveryReceipt_Server))]
    internal static class DeliveryReceiptPatch
    {
        private static void Postfix(DeliveryReceipt receipt)
        {
            DeliveryEnhancerMod.Instance?.OnReceiptRecorded(receipt);
        }
    }

    [HarmonyPatch(typeof(DeliveryApp), nameof(DeliveryApp.SetOpen))]
    internal static class DeliveryAppSetOpenPatch
    {
        private static void Postfix(DeliveryApp __instance, bool open)
        {
            if (!open || __instance == null)
                return;

            var ui = __instance.gameObject.GetComponent<DeliveryTemplateCategoryUI>();
            if (ui == null)
                ui = __instance.gameObject.AddComponent<DeliveryTemplateCategoryUI>();
            ui.Initialize(__instance);
            ui.ForceRefresh();
        }
    }

    /// <summary>
    /// Runs after <see cref="DeliveryApp.RefreshContent"/> so nested scroll content height matches
    /// what the main <see cref="DeliveryApp.MainScrollRect"/> layout coroutine just resolved.
    /// </summary>
    [HarmonyPatch(typeof(DeliveryApp), nameof(DeliveryApp.RefreshContent))]
    internal static class DeliveryAppRefreshContentPatch
    {
        private static void Postfix(DeliveryApp __instance)
        {
            if (__instance == null)
                return;
            var ui = __instance.gameObject.GetComponent<DeliveryTemplateCategoryUI>();
            ui?.ScheduleNestedScrollSync();
        }
    }

    /// <summary>Vanilla removes an active row here but does not run <see cref="DeliveryApp.RefreshContent"/> — nested layout then stays wrong.</summary>
    [HarmonyPatch(typeof(DeliveryApp), "DeliveryCompleted")]
    internal static class DeliveryAppDeliveryCompletedPatch
    {
        private static void Postfix(DeliveryApp __instance)
        {
            if (__instance == null)
                return;
            __instance.RefreshContent(true);
            __instance.gameObject.GetComponent<DeliveryTemplateCategoryUI>()?.ScheduleNestedScrollSync();
        }
    }

    [HarmonyPatch(typeof(DeliveryApp), "SortStatusDisplays")]
    internal static class DeliveryAppSortStatusDisplaysPatch
    {
        private static void Postfix(DeliveryApp __instance)
        {
            if (__instance == null)
                return;
            __instance.gameObject.GetComponent<DeliveryTemplateCategoryUI>()?.ScheduleNestedScrollSync();
        }
    }

    [HarmonyPatch(typeof(DeliveryApp), "CreateDeliveryStatusDisplay")]
    internal static class DeliveryAppCreateDeliveryStatusDisplayPatch
    {
        private static void Postfix(DeliveryApp __instance)
        {
            if (__instance == null)
                return;
            __instance.gameObject.GetComponent<DeliveryTemplateCategoryUI>()?.ScheduleNestedScrollSync();
        }
    }

    /// <summary>Price labels belong only on template rows; strip any leftover amount child from active deliveries.</summary>
    internal static class DeliveryStatusDisplayAmountCleanup
    {
        internal static void RemoveIfActiveRow(DeliveryStatusDisplay dsd)
        {
            if (dsd == null || dsd.Rect == null)
                return;
            if (dsd.gameObject.name.StartsWith("S1DE_", StringComparison.Ordinal))
                return;

            var t = dsd.Rect.Find("S1DE_AmountLabel");
            if (t != null)
                UnityEngine.Object.Destroy(t.gameObject);
        }
    }

    [HarmonyPatch(typeof(DeliveryStatusDisplay), nameof(DeliveryStatusDisplay.AssignDelivery))]
    internal static class DeliveryStatusDisplayAssignDeliveryPatch
    {
        private static void Postfix(DeliveryStatusDisplay __instance)
        {
            DeliveryStatusDisplayAmountCleanup.RemoveIfActiveRow(__instance);
        }
    }

    [HarmonyPatch(typeof(DeliveryStatusDisplay), nameof(DeliveryStatusDisplay.RefreshStatus))]
    internal static class DeliveryStatusDisplayRefreshStatusPatch
    {
        private static void Postfix(DeliveryStatusDisplay __instance)
        {
            DeliveryStatusDisplayAmountCleanup.RemoveIfActiveRow(__instance);
        }
    }

    internal sealed class DeliveryTemplateCategoryUI : MonoBehaviour
    {
        private DeliveryApp _app;
        private RectTransform _statusContainer;
        private ScrollRect _statusScrollRect;
        private bool _rightColumnScrollReady;
        private Coroutine _nestedScrollSyncCo;
        private int _lastUiRevision = -1;
        private int _pendingNativeRefreshFrames;
        private Font _uiFont;
        private Color _headerColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);
        private Color _primaryColor = new Color(0.9f, 0.9f, 0.9f, 1f);

        private enum TemplateSectionKind
        {
            Favorites,
            Recurring,
            Previous
        }

        public void Initialize(DeliveryApp app)
        {
            _app = app;
            if (_statusContainer != null)
                return;
            _statusContainer = _app.StatusDisplayContainer;

            EnsureRightColumnScroll();
            CaptureNativeStyle();
        }

        /// <summary>
        /// Nests <see cref="DeliveryApp.StatusDisplayContainer"/> in a local ScrollRect + mask so the
        /// delivery list matches vanilla phone UX (scrollbar on the right column only).
        /// </summary>
        private void EnsureRightColumnScroll()
        {
            if (_rightColumnScrollReady || _statusContainer == null)
                return;

            var parent = _statusContainer.parent as RectTransform;
            if (parent == null)
                return;

            const float scrollbarWidth = 12f;
            int siblingIndex = _statusContainer.GetSiblingIndex();
            var contentRt = _statusContainer;
            var anchorMin = contentRt.anchorMin;
            var anchorMax = contentRt.anchorMax;
            var pivot = contentRt.pivot;
            var sizeDelta = contentRt.sizeDelta;
            var anchoredPosition = contentRt.anchoredPosition;

            var scrollGo = new GameObject("S1DE_RightColumnScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.SetParent(parent, false);
            scrollRt.SetSiblingIndex(siblingIndex);
            scrollRt.anchorMin = anchorMin;
            scrollRt.anchorMax = anchorMax;
            scrollRt.pivot = pivot;
            scrollRt.sizeDelta = sizeDelta;
            scrollRt.anchoredPosition = anchoredPosition;
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            scrollGo.GetComponent<Image>().raycastTarget = true;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var vpRt = viewportGo.GetComponent<RectTransform>();
            vpRt.SetParent(scrollRt, false);
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = Vector2.zero;
            vpRt.offsetMax = new Vector2(-scrollbarWidth, 0f);
            viewportGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewportGo.GetComponent<Image>().raycastTarget = true;

            var sbGo = new GameObject("ScrollbarVertical", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var sbRtRect = sbGo.GetComponent<RectTransform>();
            sbRtRect.SetParent(scrollRt, false);
            sbRtRect.anchorMin = new Vector2(1f, 0f);
            sbRtRect.anchorMax = new Vector2(1f, 1f);
            sbRtRect.pivot = new Vector2(1f, 0.5f);
            sbRtRect.sizeDelta = new Vector2(scrollbarWidth, 0f);
            sbRtRect.anchoredPosition = Vector2.zero;
            sbGo.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.85f);

            var slidingArea = new GameObject("SlidingArea", typeof(RectTransform));
            var slidingRt = slidingArea.GetComponent<RectTransform>();
            slidingRt.SetParent(sbRtRect, false);
            slidingRt.anchorMin = Vector2.zero;
            slidingRt.anchorMax = Vector2.one;
            slidingRt.offsetMin = new Vector2(2f, 4f);
            slidingRt.offsetMax = new Vector2(-2f, -4f);

            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handleRt = handleGo.GetComponent<RectTransform>();
            handleRt.SetParent(slidingRt, false);
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;
            handleGo.GetComponent<Image>().color = new Color(0.5f, 0.5f, 0.5f, 0.95f);

            var scrollbar = sbGo.GetComponent<Scrollbar>();
            scrollbar.handleRect = handleRt;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            contentRt.SetParent(vpRt, false);
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, contentRt.sizeDelta.y);

            var sr = scrollGo.GetComponent<ScrollRect>();
            sr.viewport = vpRt;
            sr.content = contentRt;
            sr.verticalScrollbar = scrollbar;
            sr.horizontal = false;
            sr.vertical = true;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _statusScrollRect = sr;
            _rightColumnScrollReady = true;

            MirrorScrollPhysicsFromMain(sr);

            // Let Unity drive scroll-derived content height from row LayoutElements (scrollbar thumb size).
            var legacyLe = contentRt.GetComponent<LayoutElement>();
            if (legacyLe != null)
                Destroy(legacyLe);

            var vlg = contentRt.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = contentRt.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(0, 0, 4, 4);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;

            var csf = contentRt.GetComponent<ContentSizeFitter>();
            if (csf == null)
                csf = contentRt.gameObject.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void MirrorScrollPhysicsFromMain(ScrollRect nested)
        {
            if (nested == null)
                return;

            if (_app?.MainScrollRect == null)
            {
                nested.movementType = ScrollRect.MovementType.Elastic;
                nested.elasticity = 0.1f;
                nested.inertia = true;
                nested.decelerationRate = 0.135f;
                nested.scrollSensitivity = 1f;
                return;
            }

            var main = _app.MainScrollRect;
            nested.movementType = main.movementType;
            nested.elasticity = main.elasticity;
            nested.inertia = main.inertia;
            nested.decelerationRate = main.decelerationRate;
            nested.scrollSensitivity = main.scrollSensitivity;
        }

        /// <summary>
        /// Called after <see cref="DeliveryApp.RefreshContent"/> posts — that method uses a coroutine,
        /// so we wait frames before recomputing nested scroll metrics (matches MainScrollRect timing).
        /// </summary>
        internal void ScheduleNestedScrollSync()
        {
            if (!isActiveAndEnabled || _statusScrollRect == null || _statusContainer == null)
                return;
            if (_nestedScrollSyncCo != null)
                StopCoroutine(_nestedScrollSyncCo);
            _nestedScrollSyncCo = StartCoroutine(CoNestedScrollSyncAfterMainLayout());
        }

        private IEnumerator CoNestedScrollSyncAfterMainLayout()
        {
            yield return null;
            yield return null;
            NestedScrollSyncNow();
            _nestedScrollSyncCo = null;
        }

        private void LateUpdate()
        {
            ForceRefresh();
            if (_pendingNativeRefreshFrames > 0)
            {
                _pendingNativeRefreshFrames--;
                ForceNativeLayoutRefresh();
            }
        }

        public void ForceRefresh()
        {
            var mod = DeliveryEnhancerMod.Instance;
            if (_statusContainer == null || mod == null)
                return;

            var currentRevision = mod.GetUiRevision();
            if (currentRevision == _lastUiRevision)
                return;

            _lastUiRevision = currentRevision;
            RebuildAll(mod);
        }

        private void RebuildAll(DeliveryEnhancerMod mod)
        {
            for (int i = _statusContainer.childCount - 1; i >= 0; i--)
            {
                Transform child = _statusContainer.GetChild(i);
                if (child != null && child.name.StartsWith("S1DE_", StringComparison.Ordinal))
                    Destroy(child.gameObject);
            }

            var all = mod.GetTemplateRecordsLatestFirst();
            if (all.Count == 0)
            {
                if (_app != null)
                {
                    NestedScrollSyncNow();
                    ForceNativeLayoutRefresh();
                    _pendingNativeRefreshFrames = 2;
                    ScheduleNestedScrollSync();
                }

                return;
            }

            var favorites = all.Where(x => mod.IsFavoriteSignature(x.Signature)).ToList();
            var recurring = all.Where(x => mod.IsRecurringSignature(x.Signature)).ToList();
            var previous = all.Where(x => !mod.IsFavoriteSignature(x.Signature) && !mod.IsRecurringSignature(x.Signature)).ToList();

            CreateSection("Favorites", favorites, mod, TemplateSectionKind.Favorites);
            CreateSection("Recurring", recurring, mod, TemplateSectionKind.Recurring);
            CreateSection("Previous", previous, mod, TemplateSectionKind.Previous);

            if (_app != null)
            {
                NestedScrollSyncNow();
                ForceNativeLayoutRefresh();
                _pendingNativeRefreshFrames = 2;
                ScheduleNestedScrollSync();
            }
        }

        private void CaptureNativeStyle()
        {
            _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var sample = _app.StatusDisplayContainer.GetComponentInChildren<DeliveryStatusDisplay>(true);
            if (sample == null)
                return;

            if (sample.ShopLabel != null && sample.ShopLabel.font != null)
                _uiFont = sample.ShopLabel.font;

            if (sample.DestinationLabel != null)
                _primaryColor = sample.DestinationLabel.color;

            if (sample.ShopLabel != null)
                _headerColor = sample.ShopLabel.color;
        }

        private void CreateSection(string title, List<DeliveryRecord> records, DeliveryEnhancerMod mod, TemplateSectionKind sectionKind)
        {
            if (records.Count == 0)
                return;

            var headerGo = new GameObject("S1DE_Header_" + title, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            headerGo.transform.SetParent(_statusContainer, false);
            var header = headerGo.GetComponent<Text>();
            header.font = _uiFont;
            header.fontSize = 16;
            header.fontStyle = FontStyle.Bold;
            header.color = _headerColor;
            header.alignment = TextAnchor.MiddleLeft;
            header.text = title;
            var headerLe = headerGo.GetComponent<LayoutElement>();
            headerLe.preferredHeight = 24f;
            headerLe.minHeight = 24f;

            foreach (var record in records)
                CreateTemplateEntry(record, mod, sectionKind);
        }

        private void EnsureDeliveryRowLayoutHeights()
        {
            if (_statusContainer == null)
                return;

            for (int i = 0; i < _statusContainer.childCount; i++)
            {
                var rt = _statusContainer.GetChild(i) as RectTransform;
                if (rt == null || !rt.gameObject.activeSelf)
                    continue;

                var row = rt.GetComponent<DeliveryStatusDisplay>();
                if (row == null || row.Rect == null)
                    continue;

                // AssignDelivery sets sizeDelta.y; rect.height is often wrong before layout (overlap bug).
                float h = Mathf.Max(1f, row.Rect.sizeDelta.y);
                if (h <= 1f && row.DeliveryInstance != null && row.DeliveryInstance.Items != null)
                {
                    int rows = Mathf.CeilToInt((float)row.DeliveryInstance.Items.Length / 2f);
                    h = 70f + 20f * rows;
                }

                var le = row.gameObject.GetComponent<LayoutElement>();
                if (le == null)
                    le = row.gameObject.AddComponent<LayoutElement>();
                le.minHeight = h;
                le.preferredHeight = h;
            }
        }

        private void NestedScrollSyncNow()
        {
            if (_statusContainer == null || _statusScrollRect == null)
                return;

            EnsureDeliveryRowLayoutHeights();

            LayoutRebuilder.ForceRebuildLayoutImmediate(_statusContainer);
            if (_statusScrollRect.viewport != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_statusScrollRect.viewport);

            var scrollRt = _statusScrollRect.transform as RectTransform;
            if (scrollRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRt);

            Canvas.ForceUpdateCanvases();

            // ScrollRect updates scrollbar thumb once content layout size is known.
            _statusScrollRect.verticalNormalizedPosition = 1f;
        }

        private void ForceNativeLayoutRefresh()
        {
            if (_app == null)
                return;

            // Follow DeliveryApp's own refresh path to keep scroll bounds in sync.
            _app.RefreshContent(true);
            Canvas.ForceUpdateCanvases();

            if (_app.MainLayoutGroup != null)
            {
                DeliveryApp.RefreshLayoutGroupsImmediateAndRecursive(_app.MainLayoutGroup.gameObject);
                var mainRect = _app.MainLayoutGroup.GetComponent<RectTransform>();
                if (mainRect != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(mainRect);
            }

            if (_app.MainScrollRect != null && _app.MainScrollRect.content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_app.MainScrollRect.content);
                _app.MainScrollRect.velocity = Vector2.zero;
            }
        }

        private void CreateTemplateEntry(DeliveryRecord record, DeliveryEnhancerMod mod, TemplateSectionKind sectionKind)
        {
            DeliveryStatusDisplay templateDisplay = UnityEngine.Object.Instantiate(_app.StatusDisplayPrefab, _statusContainer);
            templateDisplay.name = "S1DE_TemplateStatusDisplay";
            var fakeItems = record.Items.Select(x => new StringIntPair(x.ItemId, x.Quantity)).ToArray();
            var fakeDelivery = new DeliveryInstance(
                GUIDManager.GenerateUniqueGUID().ToString(),
                record.StoreName,
                record.DestinationCode,
                record.LoadingDockIndex,
                fakeItems,
                EDeliveryStatus.Arrived,
                0);
            templateDisplay.AssignDelivery(fakeDelivery);

            // Keep per-line item rows from AssignDelivery; price goes between destination and icons.
            if (templateDisplay.StatusLabel != null)
            {
                templateDisplay.StatusLabel.text = string.Empty;
                templateDisplay.StatusLabel.enabled = false;
            }
            if (templateDisplay.StatusTooltip != null)
                templateDisplay.StatusTooltip.text = "Saved delivery template";

            if (templateDisplay.StatusImage != null)
            {
                templateDisplay.StatusImage.color = new Color(0f, 0f, 0f, 0f);
                templateDisplay.StatusImage.enabled = false;
            }

            BuildTemplateTopSummaryRow(templateDisplay, record, mod, sectionKind);

            if (templateDisplay.Rect != null)
            {
                var baseSize = _app.StatusDisplayPrefab.Rect != null ? _app.StatusDisplayPrefab.Rect.sizeDelta : templateDisplay.Rect.sizeDelta;
                templateDisplay.Rect.sizeDelta = new Vector2(baseSize.x, Mathf.Max(baseSize.y, templateDisplay.Rect.sizeDelta.y));
                var le = templateDisplay.gameObject.GetComponent<LayoutElement>();
                if (le == null)
                    le = templateDisplay.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = templateDisplay.Rect.sizeDelta.y;
                le.minHeight = templateDisplay.Rect.sizeDelta.y;
            }
        }

        /// <summary>Top-right action icons only (no amount for now).</summary>
        private void BuildTemplateTopSummaryRow(DeliveryStatusDisplay templateDisplay, DeliveryRecord record, DeliveryEnhancerMod mod, TemplateSectionKind sectionKind)
        {
            if (templateDisplay.Rect == null)
                return;

            var buttonsRoot = new GameObject("S1DE_RowButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            var buttonsRect = buttonsRoot.GetComponent<RectTransform>();
            buttonsRect.SetParent(templateDisplay.Rect, false);
            buttonsRect.anchorMin = new Vector2(1f, 1f);
            buttonsRect.anchorMax = new Vector2(1f, 1f);
            buttonsRect.pivot = new Vector2(1f, 1f);
            buttonsRect.anchoredPosition = new Vector2(-6f, -6f);
            buttonsRect.sizeDelta = new Vector2(132f, 40f);

            var buttonsLayout = buttonsRoot.GetComponent<HorizontalLayoutGroup>();
            buttonsLayout.spacing = 6f;
            buttonsLayout.childAlignment = TextAnchor.MiddleRight;
            buttonsLayout.childControlHeight = false;
            buttonsLayout.childControlWidth = false;
            buttonsLayout.childForceExpandHeight = false;
            buttonsLayout.childForceExpandWidth = false;

            var favorite = CreateIconButton(buttonsRoot.transform, "Favorite", "★", Color.white, () => mod.ToggleFavoriteBySignature(record.Signature));
            var rebuy = CreateIconButton(buttonsRoot.transform, "Rebuy", "↺", Color.white, () => mod.RebuyBySignature(record.Signature));
            var recurring = CreateIconButton(buttonsRoot.transform, "Recurring", "↻", Color.white, () => mod.ToggleRecurringBySignature(record.Signature, 0));

            ApplySectionButtonStyles(sectionKind, favorite, rebuy, recurring);

            buttonsRect.SetAsLastSibling();
        }

        /// <summary>Which icon reads as &quot;primary&quot; for this row — matches mock (star in Favorites, loop in Recurring, re-buy in Previous).</summary>
        private static void ApplySectionButtonStyles(TemplateSectionKind section, Button favoriteBtn, Button rebuyBtn, Button recurringBtn)
        {
            switch (section)
            {
                case TemplateSectionKind.Favorites:
                    StyleIconButton(favoriteBtn, true, new Color(0.98f, 0.86f, 0.18f, 1f), 24, false);
                    StyleIconButton(rebuyBtn, true, new Color(0.28f, 0.62f, 1f, 1f), 24, false);
                    StyleIconButton(recurringBtn, false, new Color(0.78f, 0.78f, 0.78f, 1f), 26, true);
                    break;
                case TemplateSectionKind.Recurring:
                    StyleIconButton(favoriteBtn, false, new Color(0.78f, 0.78f, 0.78f, 1f), 24, false);
                    StyleIconButton(rebuyBtn, false, new Color(0.78f, 0.78f, 0.78f, 1f), 24, false);
                    StyleIconButton(recurringBtn, true, new Color(1f, 0.52f, 0.12f, 1f), 30, true);
                    break;
                default:
                    StyleIconButton(favoriteBtn, false, new Color(0.78f, 0.78f, 0.78f, 1f), 24, false);
                    StyleIconButton(rebuyBtn, true, new Color(0.28f, 0.62f, 1f, 1f), 24, false);
                    StyleIconButton(recurringBtn, false, new Color(0.78f, 0.78f, 0.78f, 1f), 26, true);
                    break;
            }
        }

        private static void StyleIconButton(Button btn, bool emphasized, Color glyphColor, int glyphFontSize, bool isRecurringGlyph)
        {
            if (btn == null)
                return;

            var image = btn.GetComponent<Image>();
            if (image != null)
            {
                image.color = new Color(0f, 0f, 0f, 0f);
                image.raycastTarget = true;
            }

            var glyphTr = btn.transform.Find("Glyph");
            var glyph = glyphTr != null ? glyphTr.GetComponent<Text>() : null;
            if (glyph != null)
            {
                if (isRecurringGlyph)
                {
                    glyph.text = "↻";
                    glyph.fontSize = glyphFontSize;
                }
                else
                {
                    glyph.fontSize = glyphFontSize;
                }

                glyph.color = glyphColor;
                glyph.fontStyle = emphasized ? FontStyle.Bold : FontStyle.Normal;
            }

            if (btn.targetGraphic == null && image != null)
                btn.targetGraphic = image;
        }

        private static Text CreateText(Transform parent, string value, int fontSize, FontStyle style, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            var owner = parent.GetComponentInParent<DeliveryTemplateCategoryUI>();
            text.font = owner != null && owner._uiFont != null ? owner._uiFont : Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.text = value;
            return text;
        }

        private static Button CreateIconButton(Transform parent, string name, string glyph, Color glyphColor, Action onPressed)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(38f, 38f);

            var image = go.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onPressed());

            var textGo = new GameObject("Glyph", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.GetComponent<Text>();
            text.text = glyph;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 24;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = glyphColor;
            text.raycastTarget = false;

            return button;
        }
    }

    public sealed class DeliveryRecord
    {
        public string Signature { get; set; }
        public string StoreName { get; set; }
        public string DestinationCode { get; set; }
        public int LoadingDockIndex { get; set; }
        public List<DeliveryLineItem> Items { get; set; } = new List<DeliveryLineItem>();
        public float TotalCost { get; set; }
        public DateTime PurchasedAtUtc { get; set; }
    }

    public sealed class DeliveryLineItem
    {
        public string ItemId { get; set; }
        public int Quantity { get; set; }
    }

    public sealed class RecurringDelivery
    {
        public string TemplateSignature { get; set; }
        public int IntervalMinutes { get; set; }
        public DateTime NextRunUtc { get; set; }
        public DateTime LastSubmittedUtc { get; set; }
    }

    public sealed class PersistedData
    {
        public List<DeliveryRecord> History { get; set; }
        public List<string> Favorites { get; set; }
        public List<RecurringDelivery> Recurring { get; set; }
    }
}

# Schedule 1 Delivery Enhancer (MelonLoader)

This project is a starter mod for the Mono beta of Schedule 1 with:

- Delivery history persistence
- Favorite delivery items
- One-click re-purchase from real delivery receipts
- Recurring delivery scheduler (template-based)

## 1) Update local reference paths

Open `Schedule1DeliveryEnhancer.csproj` and update all `<HintPath>` values to match your system:

- `MelonLoader.dll`
- `Assembly-CSharp.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.IMGUIModule.dll`

## 2) Build

```bash
dotnet build
```

Output DLL:

`bin/Debug/net472/Schedule1DeliveryEnhancer.dll`

## 3) Install

Copy the DLL into:

`<Schedule 1 folder>/Mods/`

## 4) In-app UI categories

Inside the phone delivery app, template history is shown in sections:

- Favorites
- Recurring
- Previous
- All Templates

Each template row includes:

- `★` favorite toggle
- `↻` one-click re-buy
- `◔` recurring toggle (60 min)

## 5) Hook details (already wired)

The mod patches `DeliveryManager.RecordDeliveryReceipt_Server(DeliveryReceipt)` to capture real order history.

Re-order calls use game flow:

1. `DeliveryManager.SendDelivery(...)`
2. `DeliveryManager.RecordDeliveryReceipt_Server(...)`
3. `MoneyManager.CreateOnlineTransaction(...)`

## 6) Save data location

State is saved in:

`UserData/Schedule1DeliveryEnhancer/delivery-data.json`

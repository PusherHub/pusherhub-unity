# PusherHub Unity SDK

Push notifications and in-app messages for your Unity game on **Android and iOS**, sent through your own Firebase project with [PusherHub](https://pusherhub.com).

[Website](https://pusherhub.com) · [Guides](https://pusherhub.com/docs) · [Pricing](https://pusherhub.com/pricing)

## What it does

- Registers each phone with PusherHub and keeps its FCM token up to date
- Links a phone to your player id with `Login()` — remembered across restarts until `Logout()`
- Reports notification opens and clicks; a tapped notification opens its deep link (or your own handler)
- Shows in-app messages (banner, modal, full screen, center popup, bottom sheet) with Unity UI, on top of your game
- Sets up the Android manifest and the Xcode project for you during the build — nothing to edit by hand

The SDK is a single file, [`Assets/PusherHub/PusherHub.cs`](Assets/PusherHub/PusherHub.cs) (namespace `PusherHubSDK`).

## Before you start

1. A PusherHub account with your app added — it's free to start at [pusherhub.com](https://pusherhub.com).
2. Your game added to a Firebase project, with the Firebase service account key uploaded to your app in PusherHub. For iOS, your APNs key (.p8) uploaded to Firebase too. See the [getting-started guide](https://pusherhub.com/docs/getting-started) and the [Apple push guide](https://pusherhub.com/docs/ios-apns).
3. Your **app key** and **public key** — on your app's **SDK Integration** tab in PusherHub, which also shows every step below with your keys filled in.

## Install

1. Import **FirebaseMessaging.unitypackage** from the [Firebase Unity SDK](https://firebase.google.com/download/unity) — the same version as any Firebase packages already in your project.
   - Android: *Assets › External Dependency Manager › Android Resolver › Force Resolve*
   - iOS: install CocoaPods on the Mac that builds
2. Put `google-services.json` (Android) and/or `GoogleService-Info.plist` (iOS) in `Assets/`.
3. Copy [`Assets/PusherHub/PusherHub.cs`](Assets/PusherHub/PusherHub.cs) into your project's `Assets/PusherHub/` folder.
4. iOS only: *Player Settings › iOS*: your bundle identifier, your Apple Developer team, minimum iOS 15.0.

## Quick start

`Assets/Scripts/PusherHubSetup.cs`:

```csharp
using PusherHubSDK;
using UnityEngine;

public static class PusherHubSetup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        PusherHub.Initialize(
            appKey: "YOUR_APP_KEY",
            publicKey: "YOUR_PUBLIC_KEY",
            baseUrl: "https://pusherhub.com/api");

        // Required: registers this phone. Optional details once that's done.
        PusherHub.RegisterDevice(onComplete: () =>
        {
            PusherHub.CollectDeviceMetadata();
        });

        // Optional: shows in-app messages.
        PusherHub.EnableInAppMessages();
        PusherHub.CheckForInAppMessages();
    }
}
```

To handle a tapped notification's deep link inside your game instead of opening it, pass `onNotificationClick: link => { ... }` to `Initialize()`.

## Signed-in players

```csharp
PusherHub.Login(currentUserId);   // when a player signs in
PusherHub.Logout();               // when they sign out
```

## Keys

The **public key** is meant to be built into your game. Never put your **secret key** in a game — it's only for your own server, with PusherHub's REST API.

## Help

- Step-by-step guides: [pusherhub.com/docs](https://pusherhub.com/docs)
- Your app's **SDK Integration** tab in PusherHub has every step with your keys filled in.

## License

[MIT](LICENSE)

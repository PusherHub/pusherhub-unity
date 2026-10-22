// PusherHub push notification + in-app messaging client for Unity.
//
// Required setup (this is the whole "core" tier — everything else on this
// class is optional), from any script that runs once when the game starts:
//
//   using PusherHubSDK;
//
//   public static class PusherHubSetup
//   {
//       [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
//       static void Init()
//       {
//           PusherHub.Initialize(appKey: "...", publicKey: "...", baseUrl: "...");
//           PusherHub.RegisterDevice();
//       }
//   }
//
// Pushes travel through Firebase Cloud Messaging, so the project needs the
// Firebase Unity SDK's FirebaseMessaging package (the same version as any
// other Firebase package already in the project) and google-services.json
// in Assets/ — see the SDK Integration tab in the PusherHub dashboard.
//
// This whole SDK is one file (this one) — drop it anywhere under Assets/,
// e.g. Assets/PusherHub/PusherHub.cs. It needs nothing else: notifications
// shown while the game is open use Android's own APIs, and in-app messages
// are drawn with Unity UI. It also patches the generated Android manifest at
// build time (see PusherHubAndroidManifestPatcher at the bottom), so there's
// no manifest to edit by hand.
//
// Android only for now. The file compiles for iOS builds, but iOS hasn't
// been tested yet.
//
// It runs alongside another push SDK such as OneSignal: PusherHub leaves that
// SDK's pushes to it.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Firebase;
using Firebase.Extensions;
using Firebase.Messaging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace PusherHubSDK
{
    public static class PusherHub
    {
        const string AnonymousIdKey = "pushhub_anonymous_id_v2";

        // The signed-in user's ID from Login() (or RegisterDevice(externalId)),
        // kept across restarts until Logout(): without it, the next launch's
        // plain RegisterDevice() quietly turned the device anonymous again.
        const string ExternalIdKey = "pushhub_external_id";

        // Survives restarts so a denial stays distinguishable from a fresh
        // install that has never been asked — see NotificationPermissionStatus().
        const string PermissionPromptedKey = "pushhub_notification_permission_prompted";

        // The last value the backend successfully accepted, so a resume that
        // found nothing changed costs nothing.
        const string LastReportedPermissionKey = "pushhub_last_reported_notification_permission";

        // The last FCM token the backend accepted. FCM rotates tokens without a
        // reinstall; reporting the one being replaced lets the backend keep this
        // install as the same device rather than counting a new install plus,
        // later, an uninstall. PlayerPrefs is wiped on uninstall, which is
        // exactly when a new device row is the right answer.
        const string LastRegisteredTokenKey = "pushhub_last_registered_token";

        // The last notification tap already reported, so an Activity that
        // Android recreates with its original launch Intent doesn't report
        // the same tap twice.
        const string LastHandledTapKey = "pushhub_last_handled_tap";

        /// <summary>
        /// The action PusherHub's backend hardcodes into every FCM payload's
        /// android.notification.click_action (a holdover from the Flutter SDK).
        /// A background/killed-state notification tap only reaches the game when
        /// its launcher Activity has an intent-filter for it — which
        /// PusherHubAndroidManifestPatcher adds at build time.
        /// </summary>
        public const string ActionNotificationClick = "FLUTTER_NOTIFICATION_CLICK";

        internal const string ChannelId = "pushhub_default";
        internal const string ExtraNotificationId = "pushhub_notification_id";
        internal const string ExtraDeepLink = "pushhub_deep_link";
        internal const string ExtraTapId = "pushhub_tap_id";

        // Matches the window the backend's Realtime tab buckets by. Anything
        // faster just upserts the same minute row for no extra information.
        const float HeartbeatIntervalSeconds = 60f;

        static string appKey;
        static string publicKey;
        static string baseUrl;
        static Action<string> onNotificationClick;
        static bool initialized;
        static bool firebaseReady;
        static PusherHubRunner runner;

        static string currentToken;
        static string currentExternalId;

        // RegisterDevice() was called, so a token arriving later (or rotating)
        // should be registered. Before that, a token Firebase hands over at
        // startup is only remembered — the game hasn't asked to register yet.
        static bool registrationRequested;

        // RegisterDevice() is waiting on Firebase for the token.
        static bool waitingForToken;
        static readonly List<Action> waitingCompletions = new List<Action>();

        // True from the moment the notification prompt is shown until its
        // outcome is observed. Notifications read as disabled while the dialog
        // is still up, which would otherwise be reported as a denial the user
        // hasn't actually made yet.
        static bool notificationPromptInFlight;

        // The prompt pauses the game for as long as it's up, so "resumed,
        // having been paused since the prompt went out" means it was answered.
        static bool pausedSinceNotificationPrompt;

#if UNITY_IOS && !UNITY_EDITOR
        // What the iOS prompt answered — iOS has no Android-style query that
        // plain C# can reach.
        static string iosPermission = "not_determined";
#endif

        // Collected once (CollectDeviceMetadata) and re-sent on every later
        // registration — the backend's register-device endpoint replaces these
        // fields wholesale, so omitting them on a later call would wipe out
        // whatever was already stored server-side.
        static string deviceModel;
        static string deviceBrand;
        static string appVersion;
        static string language;
        static string country;
        static string timezone;

        // Taps on a fresh install the backend doesn't know yet — track-open
        // looks the device up by token. Sent once registration succeeds.
        static readonly List<KeyValuePair<string, string>> pendingOpens = new List<KeyValuePair<string, string>>();

        static bool appPaused;
        static Coroutine heartbeat;

        static bool inAppAutoCheck;
        static bool inAppCheckWaitingForRegistration;
        static bool inAppFetchInFlight;
        static readonly List<InAppMessage> inAppQueue = new List<InAppMessage>();
        static readonly HashSet<int> inAppShownThisRun = new HashSet<int>();
        static PusherHubInAppView inAppView;

        // Firebase raises its events off Unity's main thread; everything that
        // touches Unity or Android goes through here first.
        static readonly Queue<Action> mainThreadActions = new Queue<Action>();

        /// <summary>The identity currently registered with the backend — null until RegisterDevice() completes.</summary>
        public static string ExternalId => currentExternalId;

        /// <summary>
        /// Call once, as early as possible — a [RuntimeInitializeOnLoadMethod]
        /// in any script is the easiest place, since it needs no GameObject in
        /// a scene. Creates a hidden "PusherHub" GameObject that lives for the
        /// whole session.
        /// </summary>
        /// <param name="baseUrl">
        /// Your PusherHub server's API base URL: "https://pusherhub.com/api" for
        /// apps on pusherhub.com — the dashboard's SDK Integration tab shows it
        /// filled in. There's deliberately no default — a guessed one would
        /// silently point at the wrong server and RegisterDevice() would fail
        /// with nothing obviously wrong in your own code.
        /// </param>
        /// <param name="onNotificationClick">
        /// Optional — called instead of the SDK's default behavior
        /// (Application.OpenURL) when a tapped notification or in-app message
        /// carries a deep link.
        /// </param>
        public static void Initialize(string appKey, string publicKey, string baseUrl, Action<string> onNotificationClick = null)
        {
            if (initialized)
            {
                Log("Initialize() was already called — ignoring this call.");
                return;
            }

            PusherHub.appKey = appKey;
            PusherHub.publicKey = publicKey;
            PusherHub.baseUrl = (baseUrl ?? "").TrimEnd('/');
            PusherHub.onNotificationClick = onNotificationClick;
            initialized = true;

            var host = new GameObject("PusherHub");
            UnityEngine.Object.DontDestroyOnLoad(host);
            runner = host.AddComponent<PusherHubRunner>();

            if (!IsSupportedPlatform)
            {
                Log("Push notifications only work in an Android build on a real device — PusherHub does nothing here.");
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            PusherHubAndroid.CreateNotificationChannel();
#endif

            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Log("Firebase couldn't start: " + task.Exception);
                    return;
                }
                if (task.Result != DependencyStatus.Available)
                {
                    Log("Firebase isn't available (" + task.Result + ") — check google-services.json and the Firebase packages. PusherHub stays disabled until then.");
                    return;
                }

                firebaseReady = true;
                FirebaseMessaging.TokenReceived += OnTokenReceived;
                FirebaseMessaging.MessageReceived += OnMessageReceived;

                if (waitingForToken)
                {
                    RequestNotificationPermissionIfNeeded();
                    FetchToken();
                }
            });

            // A tap that cold-launched the game is on the launch Intent already.
            HandleNotificationTap();
        }

        /// <summary>
        /// Asks for notification permission (Android 13+), gets this device's
        /// FCM token, and registers it with the PusherHub backend, as the user
        /// last signed in with Login() — remembered across restarts until
        /// Logout() — or, with nobody signed in, a persistent anonymous ID
        /// generated on first launch (matching OneSignal's default
        /// anonymous-subscription behavior). Passing externalId here is the
        /// same as calling Login() with it.
        ///
        /// Registration finishes asynchronously (it waits on Firebase for the
        /// token), so pass onComplete if you need to sequence something after
        /// it — e.g. <c>PusherHub.RegisterDevice(onComplete: PusherHub.CollectDeviceMetadata)</c> —
        /// rather than calling that right after this returns.
        /// </summary>
        public static void RegisterDevice(string externalId = null, Action onComplete = null)
        {
            if (!RequireInitialized() || !IsSupportedPlatform) return;

            registrationRequested = true;
            if (externalId != null) RememberExternalId(externalId);
            if (onComplete != null) waitingCompletions.Add(onComplete);
            waitingForToken = true;

            // Before Firebase is ready, both happen as soon as it is.
            if (!firebaseReady) return;
            RequestNotificationPermissionIfNeeded();
            FetchToken();
        }

        /// <summary>
        /// Aliases this device to a real signed-in user — equivalent to
        /// <c>OneSignal.Login(externalId)</c> — and remembers it on the device,
        /// so every later launch registers as this user too, until Logout().
        /// Re-registers the same FCM token under the new external_id, so the
        /// backend re-links the existing device row instead of creating a
        /// duplicate. Called before RegisterDevice() has finished, the ID is
        /// still remembered and used as soon as it does.
        /// </summary>
        public static void Login(string externalId)
        {
            if (!RequireInitialized()) return;
            RememberExternalId(externalId);
            // A registration still on its way reads the remembered ID itself.
            if (currentToken == null || waitingForToken) return;
            RegisterToken(externalId, currentToken, null);
        }

        /// <summary>Reverts this device back to its anonymous identity, and forgets the signed-in user — call this on sign-out.</summary>
        public static void Logout()
        {
            if (!RequireInitialized()) return;
            PlayerPrefs.DeleteKey(ExternalIdKey);
            PlayerPrefs.Save();
            if (currentToken == null || waitingForToken) return;
            RegisterToken(AnonymousId(), currentToken, null);
        }

        /// <summary>
        /// Optional: enriches this device's record with brand, model, game
        /// version, language, country, and timezone — none of which need a
        /// permission. The backend estimates state from the device's IP
        /// address. Safe to call any time after RegisterDevice() has
        /// completed; skips quietly if called before that.
        /// </summary>
        public static void CollectDeviceMetadata()
        {
            if (currentToken == null)
            {
                Log("CollectDeviceMetadata() called before RegisterDevice() completed.");
                return;
            }

            appVersion = Application.version;
#if UNITY_ANDROID && !UNITY_EDITOR
            PusherHubAndroid.ReadDeviceInfo(out deviceBrand, out deviceModel, out language, out country, out timezone);
#else
            deviceBrand = Application.platform == RuntimePlatform.IPhonePlayer ? "Apple" : null;
            deviceModel = SystemInfo.deviceModel;
            // Sent as raw codes ("en", "US") — the backend translates these to
            // full names, so the SDK doesn't need to ship its own lookup tables.
            language = NullIfEmpty(CultureInfo.CurrentCulture.TwoLetterISOLanguageName);
            try
            {
                country = NullIfEmpty(new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName);
            }
            catch (ArgumentException)
            {
                country = null;
            }
#if UNITY_IOS && !UNITY_EDITOR
            timezone = PusherHubIOS.TimeZoneName();
#else
            timezone = NullIfEmpty(TimeZoneInfo.Local.Id);
#endif
#endif
            Reregister();
        }

        // -----------------------------------------------------------------
        // In-app messages
        // -----------------------------------------------------------------

        /// <summary>
        /// Optional: shows eligible in-app messages now and every time the
        /// game comes back to the foreground — the Unity equivalent of the
        /// Flutter SDK's PusherHubMessageHost. Call it once, right after
        /// RegisterDevice(). Games that only want messages at calm moments
        /// (say, the main menu) call CheckForInAppMessages() there instead.
        /// </summary>
        public static void EnableInAppMessages()
        {
            inAppAutoCheck = true;
            CheckForInAppMessages();
        }

        /// <summary>
        /// Fetches in-app messages and shows the eligible ones, one after
        /// another, styled to match their configured display type (banner/
        /// modal/fullscreen/center_popup/bottom_sheet) with simple or blocks
        /// content — the same content an admin authors on the PusherHub
        /// dashboard. Shown/click/dismiss are tracked automatically. Messages
        /// built in HTML mode are skipped: Unity has no built-in web view.
        ///
        /// Calling this before registration has finished is fine — the check
        /// runs as soon as it does.
        /// </summary>
        public static void CheckForInAppMessages()
        {
            if (!initialized || !IsSupportedPlatform) return;
            // The game starts long before the first launch's token arrives —
            // remember the request instead of losing this launch.
            if (currentToken == null || currentExternalId == null)
            {
                inAppCheckWaitingForRegistration = true;
                return;
            }
            if (inAppFetchInFlight) return;

            inAppFetchInFlight = true;
            FetchInAppMessages(messages =>
            {
                inAppFetchInFlight = false;
                foreach (var message in messages)
                {
                    if (message.ContentMode == "html")
                    {
                        Log("Skipping in-app message " + message.Id + ": HTML content can't be shown in Unity.");
                        continue;
                    }
                    if (inAppShownThisRun.Contains(message.Id) || inAppQueue.Exists(m => m.Id == message.Id)) continue;
                    inAppQueue.Add(message);
                }
                ShowNextInAppMessageIfIdle();
            });
        }

        /// <summary>
        /// Fetches the in-app messages currently eligible for this device —
        /// active, within their display window, matching this device's
        /// audience, and not already shown once (for messages set to display
        /// only once). Prefer CheckForInAppMessages() if you just want the
        /// SDK's own rendering — call this instead only to build entirely
        /// custom UI from the raw data, then call TrackInAppShown/
        /// TrackInAppClick/TrackInAppDismiss yourself as appropriate.
        /// </summary>
        public static void FetchInAppMessages(Action<List<InAppMessage>> callback)
        {
            if (currentToken == null)
            {
                Log("Skipping FetchInAppMessages — no FCM token registered yet.");
                callback(new List<InAppMessage>());
                return;
            }

            var body = new Dictionary<string, object> { { "app_key", appKey }, { "token", currentToken } };
            Post("fetch-in-app-messages", body, response =>
            {
                var messages = new List<InAppMessage>();
                if (response != null && response.TryGetValue("messages", out var list) && list is List<object> items)
                {
                    foreach (var item in items)
                    {
                        var message = item is Dictionary<string, object> json ? InAppMessage.FromJson(json) : null;
                        if (message != null) messages.Add(message);
                    }
                }
                Log("Fetched " + messages.Count + " eligible in-app message(s).");
                callback(messages);
            });
        }

        /// <summary>Reports that the message was actually displayed to the user.</summary>
        public static void TrackInAppShown(InAppMessage message) => TrackInAppEvent("track-in-app-shown", message.Id);

        /// <summary>Reports that the user tapped the message's button or link.</summary>
        public static void TrackInAppClick(InAppMessage message) => TrackInAppEvent("track-in-app-click", message.Id);

        /// <summary>Reports that the message was closed without the user acting on it.</summary>
        public static void TrackInAppDismiss(InAppMessage message) => TrackInAppEvent("track-in-app-dismiss", message.Id);

        static void TrackInAppEvent(string endpoint, int inAppMessageId)
        {
            if (currentToken == null)
            {
                Log("Skipping " + endpoint + " — no FCM token registered yet.");
                return;
            }
            Post(endpoint, new Dictionary<string, object>
            {
                { "app_key", appKey },
                { "in_app_message_id", inAppMessageId },
                { "token", currentToken },
            }, null);
        }

        static void ShowNextInAppMessageIfIdle()
        {
            // Not on screen right now — the queue resumes on the next foreground.
            if (inAppView != null || inAppQueue.Count == 0 || appPaused) return;

            var message = inAppQueue[0];
            inAppQueue.RemoveAt(0);
            inAppShownThisRun.Add(message.Id);
            TrackInAppShown(message);

            inAppView = PusherHubInAppView.Show(message, (outcome, link) =>
            {
                inAppView = null;
                switch (outcome)
                {
                    case InAppOutcome.Closed:
                        // An explicit close (✕, or a close action) is the user
                        // answering the message, not ignoring it — matching the
                        // other SDKs, it isn't a dismiss.
                        break;
                    case InAppOutcome.Dismissed:
                        TrackInAppDismiss(message);
                        break;
                    case InAppOutcome.Opened:
                        TrackInAppClick(message);
                        OpenDeepLink(link);
                        break;
                }
                // A beat between messages, which also lets a link that just
                // opened another app take the game out of the foreground first
                // — the next message then waits for the player to come back
                // instead of being counted as shown to nobody.
                runner.StartCoroutine(AfterDelay(0.6f, ShowNextInAppMessageIfIdle));
            });
        }

        // -----------------------------------------------------------------
        // Registration
        // -----------------------------------------------------------------

        static void FetchToken()
        {
            FirebaseMessaging.GetTokenAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled || string.IsNullOrEmpty(task.Result))
                {
                    Log("Failed to retrieve the FCM token: " + task.Exception);
                    return;
                }
                ReceivedToken(task.Result);
            });
        }

        static void OnTokenReceived(object sender, TokenReceivedEventArgs e)
        {
            var token = e.Token;
            RunOnMainThread(() => ReceivedToken(token));
        }

        static void ReceivedToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            var previous = currentToken;
            currentToken = token;

            if (waitingForToken)
            {
                waitingForToken = false;
                var completions = waitingCompletions.ToArray();
                waitingCompletions.Clear();
                // Read now, not when RegisterDevice() was called: a Login() or
                // Logout() while the token was on its way counts.
                RegisterToken(ResolvedExternalId(), token, () =>
                {
                    foreach (var completion in completions) completion();
                });
            }
            else if (registrationRequested && previous != token)
            {
                // FCM rotated the token while the game was running.
                RegisterToken(ResolvedExternalId(), token, null);
            }
        }

        /// <summary>Re-sends the current identity — after metadata changed, or the permission did.</summary>
        static void Reregister()
        {
            if (currentToken == null || waitingForToken) return;
            RegisterToken(ResolvedExternalId(), currentToken, null);
        }

        static void RegisterToken(string externalId, string token, Action completion)
        {
            var permission = NotificationPermissionStatus();
            var previousToken = PlayerPrefs.GetString(LastRegisteredTokenKey, "");

            var body = new Dictionary<string, object>
            {
                { "app_key", appKey },
                { "external_id", externalId },
                { "token", token },
                { "platform", Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "android" },
                { "notification_permission", permission },
            };
            // Covers both ways a rotation arrives: TokenReceived while running,
            // and a different token on a later launch.
            if (previousToken != "" && previousToken != token) body["previous_token"] = previousToken;
            AddIfSet(body, "device", deviceModel);
            AddIfSet(body, "brand", deviceBrand);
            AddIfSet(body, "version", appVersion);
            AddIfSet(body, "language", language);
            AddIfSet(body, "country", country);
            AddIfSet(body, "timezone", timezone);

            Post("register-device", body, response =>
            {
                if (response != null)
                {
                    currentExternalId = externalId;
                    // Only cached once the backend has actually taken it —
                    // caching a value a failed request never delivered would
                    // suppress the retry on the next resume. The token
                    // likewise: if this request failed, the next attempt must
                    // still report the old token as the one being replaced.
                    PlayerPrefs.SetString(LastReportedPermissionKey, permission);
                    PlayerPrefs.SetString(LastRegisteredTokenKey, token);
                    PlayerPrefs.Save();
                    Log("Device registered successfully (token " + Mask(token) + ").");
                    FlushPendingOpens();
                    if (inAppCheckWaitingForRegistration)
                    {
                        inAppCheckWaitingForRegistration = false;
                        CheckForInAppMessages();
                    }
                }
                else
                {
                    Log("Failed to register device.");
                }
                completion?.Invoke();
            });
        }

        // -----------------------------------------------------------------
        // Notification permission
        // -----------------------------------------------------------------

        static void RequestNotificationPermissionIfNeeded()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Below Android 13 the permission doesn't exist and notifications just work.
            if (PusherHubAndroid.SdkInt < 33) return;
            if (Permission.HasUserAuthorizedPermission(PusherHubAndroid.PostNotifications)) return;

            notificationPromptInFlight = true;
            pausedSinceNotificationPrompt = false;
            PlayerPrefs.SetInt(PermissionPromptedKey, 1);
            PlayerPrefs.Save();

            // Another plugin's prompt that's already up cancels this one —
            // harmless: the answer to that prompt is read on the next resume.
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => RunOnMainThread(SyncNotificationPermissionIfChanged);
            callbacks.PermissionDenied += _ => RunOnMainThread(SyncNotificationPermissionIfChanged);
            Permission.RequestUserPermission(PusherHubAndroid.PostNotifications, callbacks);
#elif UNITY_IOS && !UNITY_EDITOR
            if (iosPermission != "not_determined") return;
            notificationPromptInFlight = true;
            FirebaseMessaging.RequestPermissionAsync().ContinueWithOnMainThread(task =>
            {
                iosPermission = task.IsFaulted || task.IsCanceled ? "denied" : "granted";
                SyncNotificationPermissionIfChanged();
            });
#endif
        }

        /// <summary>
        /// This device's notification permission as the backend records it:
        /// "granted", "denied", or "not_determined". On Android it's built on
        /// NotificationManager.areNotificationsEnabled() rather than only the
        /// runtime permission, because a user on any version can switch the
        /// game's notifications off in system settings — and the question the
        /// dashboard answers is "will a push actually reach this device".
        /// </summary>
        static string NotificationPermissionStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (PusherHubAndroid.AreNotificationsEnabled()) return "granted";

            // Off — but on Android 13+ that's also the state of an install
            // nobody has asked yet, which isn't the same signal as a refusal.
            var neverAnswered = notificationPromptInFlight || PlayerPrefs.GetInt(PermissionPromptedKey, 0) == 0;
            return PusherHubAndroid.SdkInt >= 33 && neverAnswered ? "not_determined" : "denied";
#elif UNITY_IOS && !UNITY_EDITOR
            return iosPermission;
#else
            return "not_determined";
#endif
        }

        /// <summary>
        /// Re-reports the notification permission when it no longer matches
        /// what the backend was last told — the user answering the prompt, or
        /// turning notifications off (or back on) in system settings weeks
        /// later, which produces no callback of any kind. A no-op with no
        /// network call in the overwhelmingly common case where nothing
        /// changed, so it's safe on every resume.
        /// </summary>
        static void SyncNotificationPermissionIfChanged()
        {
            if (!initialized) return;

            // Every caller reaches here only once the prompt has resolved, so
            // what we read now is the user's actual answer.
            notificationPromptInFlight = false;

            if (!registrationRequested || currentToken == null || waitingForToken) return;
            if (NotificationPermissionStatus() == PlayerPrefs.GetString(LastReportedPermissionKey, "")) return;
            Reregister();
        }

        // -----------------------------------------------------------------
        // Incoming pushes and taps
        // -----------------------------------------------------------------

        static void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            var message = e.Message;
            RunOnMainThread(() => HandleMessage(message));
        }

        static void HandleMessage(FirebaseMessage message)
        {
            var data = message.Data ?? new Dictionary<string, string>();
            // notification_id travels in the data payload of every campaign
            // (set server-side in FcmService) and is what opens are tracked by.
            data.TryGetValue("notification_id", out var notificationId);
            notificationId = NullIfEmpty(notificationId);
            data.TryGetValue("deep_link", out var deepLink);
            deepLink = NullIfEmpty(deepLink);

            if (message.NotificationOpened)
            {
#if UNITY_IOS && !UNITY_EDITOR
                if (notificationId != null) NotificationOpened(notificationId, deepLink);
#endif
                // On Android, taps are read from the Activity's Intent instead
                // (HandleNotificationTap) — Firebase only reports them when the
                // game uses its MessagingUnityPlayerActivity, which most don't.
                return;
            }

            var title = message.Notification?.Title;
            if (title == null) data.TryGetValue("title", out title);
            var body = message.Notification?.Body;
            if (body == null) data.TryGetValue("body", out body);

            // A push without notification_id is the dashboard's Send test, or
            // one sent straight from the Firebase console — shown too, like
            // the other PusherHub SDKs do, just not tracked. Except another
            // push SDK's own, which that SDK shows itself (OneSignal marks
            // its pushes with a "custom" field), and data-only messages.
            if (notificationId == null && (data.ContainsKey("custom") || (title == null && body == null))) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            // Only reaches here while the game is in the foreground — for a
            // backgrounded or killed game, Android shows the push's
            // notification block itself and this never fires.
            PusherHubAndroid.ShowNotification(title, body, notificationId, deepLink);
#endif
        }

        /// <summary>
        /// Reports a notification tap that (re)opened the game and opens its
        /// deep link. Android hands the tap over on the Activity's Intent: the
        /// launch Intent on a cold start, and the Intent Unity swaps in on
        /// resume otherwise.
        /// </summary>
        static void HandleNotificationTap()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!PusherHubAndroid.TakeNotificationTap(out var notificationId, out var deepLink, out var tapId)) return;
            if (tapId != null)
            {
                if (tapId == PlayerPrefs.GetString(LastHandledTapKey, "")) return;
                PlayerPrefs.SetString(LastHandledTapKey, tapId);
                PlayerPrefs.Save();
            }
            NotificationOpened(notificationId, deepLink);
#endif
        }

        static void NotificationOpened(string notificationId, string deepLink)
        {
            Log("Notification opened: " + notificationId);
            TrackOpen(notificationId, deepLink);
            if (deepLink != null) OpenDeepLink(deepLink);
        }

        static void TrackOpen(string notificationId, string deepLink)
        {
            // The token the backend last accepted, saved from an earlier launch —
            // so a tap that cold-launches the game is reported straight away
            // rather than after this launch's registration.
            var token = PlayerPrefs.GetString(LastRegisteredTokenKey, "");
            if (token == "")
            {
                pendingOpens.Add(new KeyValuePair<string, string>(notificationId, deepLink));
                return;
            }
            TrackEvent("track-open", notificationId, token);
            if (deepLink != null) TrackEvent("track-click", notificationId, token);
        }

        static void FlushPendingOpens()
        {
            var pending = pendingOpens.ToArray();
            pendingOpens.Clear();
            foreach (var open in pending) TrackOpen(open.Key, open.Value);
        }

        static void TrackEvent(string endpoint, string notificationId, string token)
        {
            object id = int.TryParse(notificationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) ? numeric : (object)notificationId;
            Post(endpoint, new Dictionary<string, object> { { "app_key", appKey }, { "notification_id", id }, { "token", token } }, null);
        }

        static void OpenDeepLink(string deepLink)
        {
            if (string.IsNullOrEmpty(deepLink)) return;
            if (onNotificationClick != null)
            {
                onNotificationClick(deepLink);
                return;
            }
            Application.OpenURL(deepLink);
        }

        // -----------------------------------------------------------------
        // App lifecycle + heartbeat
        // -----------------------------------------------------------------

        internal static void AppPaused()
        {
            appPaused = true;
            if (notificationPromptInFlight) pausedSinceNotificationPrompt = true;
            if (heartbeat != null)
            {
                runner.StopCoroutine(heartbeat);
                heartbeat = null;
            }
        }

        internal static void AppResumed()
        {
            appPaused = false;
            if (!initialized || !IsSupportedPlatform) return;

            HandleNotificationTap();
            // Still waiting on an unanswered prompt — reading the permission
            // now would report a denial the user hasn't made.
            if (!notificationPromptInFlight || pausedSinceNotificationPrompt) SyncNotificationPermissionIfChanged();
            if (heartbeat == null) heartbeat = runner.StartCoroutine(Heartbeat());
            if (inAppAutoCheck) CheckForInAppMessages();
            ShowNextInAppMessageIfIdle();
        }

        /// <summary>
        /// Tells the backend this device has the game open right now — the
        /// signal behind the dashboard's Realtime tab, and the only thing
        /// separating "playing" from register-device's "launched it at some
        /// point". Silently skipped before registration completes; the next
        /// tick picks it up once a token exists.
        /// </summary>
        static IEnumerator Heartbeat()
        {
            while (true)
            {
                if (currentToken != null)
                {
                    Post("heartbeat", new Dictionary<string, object> { { "app_key", appKey }, { "token", currentToken } }, null);
                }
                yield return new WaitForSecondsRealtime(HeartbeatIntervalSeconds);
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        static bool IsSupportedPlatform =>
            !Application.isEditor && (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer);

        /// <summary>Calls done with the parsed JSON response body, or null on any failure (network, non-2xx, bad JSON).</summary>
        static void Post(string endpoint, Dictionary<string, object> body, Action<Dictionary<string, object>> done)
        {
            runner.StartCoroutine(PostRoutine(endpoint, body, done));
        }

        static IEnumerator PostRoutine(string endpoint, Dictionary<string, object> body, Action<Dictionary<string, object>> done)
        {
            using (var request = new UnityWebRequest(baseUrl + "/" + endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(PusherHubJson.Serialize(body)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 15;
                request.SetRequestHeader("Content-Type", "application/json");
                // Without it, a validation failure comes back as a redirect
                // instead of the JSON error that says which field was wrong.
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + publicKey);
                // Harmless against a real server; needed only when baseUrl
                // points at a free-tier ngrok tunnel — without it, ngrok returns
                // an HTML "visit site" warning page instead of forwarding.
                request.SetRequestHeader("ngrok-skip-browser-warning", "true");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log(endpoint + " failed: HTTP " + request.responseCode + " " + request.error + " " + request.downloadHandler.text);
                    done?.Invoke(null);
                    yield break;
                }

                var text = request.downloadHandler.text;
                var parsed = string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : PusherHubJson.Deserialize(text) as Dictionary<string, object>;
                done?.Invoke(parsed ?? new Dictionary<string, object>());
            }
        }

        static IEnumerator AfterDelay(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            action();
        }

        static void RememberExternalId(string externalId)
        {
            PlayerPrefs.SetString(ExternalIdKey, externalId);
            PlayerPrefs.Save();
        }

        /// <summary>Who this device registers as: the signed-in user (see Login), or the anonymous ID.</summary>
        static string ResolvedExternalId()
        {
            var externalId = PlayerPrefs.GetString(ExternalIdKey, "");
            return externalId != "" ? externalId : AnonymousId();
        }

        static string AnonymousId()
        {
            var id = PlayerPrefs.GetString(AnonymousIdKey, "");
            if (id != "") return id;

            id = Guid.NewGuid().ToString();
            PlayerPrefs.SetString(AnonymousIdKey, id);
            PlayerPrefs.Save();
            return id;
        }

        static bool RequireInitialized()
        {
            if (!initialized) Log("PusherHub.Initialize() must be called before this.");
            return initialized;
        }

        static void AddIfSet(Dictionary<string, object> body, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) body[key] = value;
        }

        internal static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        static void RunOnMainThread(Action action)
        {
            lock (mainThreadActions) mainThreadActions.Enqueue(action);
        }

        internal static void DrainMainThreadQueue()
        {
            while (true)
            {
                Action action;
                lock (mainThreadActions)
                {
                    if (mainThreadActions.Count == 0) return;
                    action = mainThreadActions.Dequeue();
                }
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Every SDK log line goes through here, and only development builds
        /// print — a release build logs nothing, so push tokens, payloads, and
        /// server responses never reach the device log of a shipped game.
        /// </summary>
        internal static void Log(string message)
        {
            if (Debug.isDebugBuild) Debug.Log("PusherHub: " + message);
        }

        /// <summary>Enough of a push token to tell devices apart while debugging, without writing a usable token to the log.</summary>
        static string Mask(string token) => token.Length <= 8 ? "***" : token.Substring(0, 8) + "…";
    }

    /// <summary>Forwards Unity's frame and pause callbacks to PusherHub. Created by PusherHub.Initialize().</summary>
    [AddComponentMenu("")]
    internal sealed class PusherHubRunner : MonoBehaviour
    {
        void Start() => PusherHub.AppResumed();

        void Update() => PusherHub.DrainMainThreadQueue();

        void OnApplicationPause(bool paused)
        {
            if (paused) PusherHub.AppPaused();
            else PusherHub.AppResumed();
        }
    }

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>The few iOS system calls PusherHub makes — CoreFoundation, which every iOS app links.</summary>
    internal static class PusherHubIOS
    {
        const uint Utf8 = 0x08000100;

        [DllImport("__Internal")] static extern IntPtr CFTimeZoneCopyDefault();
        [DllImport("__Internal")] static extern IntPtr CFTimeZoneGetName(IntPtr timeZone);
        [DllImport("__Internal")] [return: MarshalAs(UnmanagedType.I1)] static extern bool CFStringGetCString(IntPtr text, byte[] buffer, long bufferSize, uint encoding);
        [DllImport("__Internal")] static extern void CFRelease(IntPtr value);

        /// <summary>
        /// The device's time zone as an IANA name ("Asia/Kolkata") — on iOS,
        /// TimeZoneInfo.Local.Id only ever says "Local".
        /// </summary>
        internal static string TimeZoneName()
        {
            var zone = CFTimeZoneCopyDefault();
            if (zone == IntPtr.Zero) return null;
            try
            {
                var buffer = new byte[128];
                var name = CFTimeZoneGetName(zone);
                if (name == IntPtr.Zero || !CFStringGetCString(name, buffer, buffer.Length, Utf8)) return null;
                var length = Array.IndexOf(buffer, (byte)0);
                return PusherHub.NullIfEmpty(Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length));
            }
            finally
            {
                CFRelease(zone);
            }
        }
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>The Android calls PusherHub makes through JNI — all on Unity's main thread.</summary>
    internal static class PusherHubAndroid
    {
        internal const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

        const int IntentFlagActivityNewTask = 0x10000000;
        const int IntentFlagActivitySingleTop = 0x20000000;
        const int PendingIntentFlagUpdateCurrent = 0x08000000;
        const int PendingIntentFlagImmutable = 0x04000000;
        const int ImportanceHigh = 4;
        const int PriorityHigh = 1;
        const int GetMetaData = 128;

        static int sdkInt = -1;

        internal static int SdkInt
        {
            get
            {
                if (sdkInt < 0)
                {
                    using (var version = new AndroidJavaClass("android.os.Build$VERSION")) sdkInt = version.GetStatic<int>("SDK_INT");
                }
                return sdkInt;
            }
        }

        static AndroidJavaObject CurrentActivity()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer")) return player.GetStatic<AndroidJavaObject>("currentActivity");
        }

        static AndroidJavaObject NotificationManager(AndroidJavaObject context) => context.Call<AndroidJavaObject>("getSystemService", "notification");

        internal static bool AreNotificationsEnabled()
        {
            // Android 6 has no switch to read — notifications are on.
            if (SdkInt < 24) return true;
            try
            {
                using (var activity = CurrentActivity())
                using (var manager = NotificationManager(activity))
                {
                    return manager.Call<bool>("areNotificationsEnabled");
                }
            }
            catch (Exception e)
            {
                PusherHub.Log("Couldn't read the notification setting: " + e.Message);
                return false;
            }
        }

        /// <summary>The channel notifications shown while the game is open are posted to — heads-up, like the other PusherHub SDKs.</summary>
        internal static void CreateNotificationChannel()
        {
            if (SdkInt < 26) return;
            try
            {
                using (var activity = CurrentActivity())
                using (var manager = NotificationManager(activity))
                using (var channel = new AndroidJavaObject("android.app.NotificationChannel", PusherHub.ChannelId, "Notifications", ImportanceHigh))
                {
                    manager.Call("createNotificationChannel", channel);
                }
            }
            catch (Exception e)
            {
                PusherHub.Log("Couldn't create the notification channel: " + e.Message);
            }
        }

        internal static void ReadDeviceInfo(out string brand, out string model, out string language, out string country, out string timezone)
        {
            using (var build = new AndroidJavaClass("android.os.Build"))
            {
                brand = PusherHub.NullIfEmpty(build.GetStatic<string>("MANUFACTURER"));
                model = PusherHub.NullIfEmpty(build.GetStatic<string>("MODEL"));
            }
            using (var localeClass = new AndroidJavaClass("java.util.Locale"))
            using (var locale = localeClass.CallStatic<AndroidJavaObject>("getDefault"))
            {
                language = PusherHub.NullIfEmpty(locale.Call<string>("getLanguage"));
                country = PusherHub.NullIfEmpty(locale.Call<string>("getCountry"));
            }
            using (var timeZoneClass = new AndroidJavaClass("java.util.TimeZone"))
            using (var timeZone = timeZoneClass.CallStatic<AndroidJavaObject>("getDefault"))
            {
                timezone = PusherHub.NullIfEmpty(timeZone.Call<string>("getID"));
            }
        }

        /// <summary>
        /// Reads (and clears, so a later resume can't report it again) the
        /// PusherHub notification tap on the Activity's current Intent. The
        /// prefixed extras are on taps of notifications this SDK posted while
        /// the game was open; for a backgrounded/killed game, Android builds
        /// the tap Intent itself from the push, carrying its raw data keys.
        /// </summary>
        internal static bool TakeNotificationTap(out string notificationId, out string deepLink, out string tapId)
        {
            notificationId = deepLink = tapId = null;
            try
            {
                using (var activity = CurrentActivity())
                {
                    if (activity == null) return false;
                    using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                    {
                        if (intent == null) return false;
                        notificationId = PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", PusherHub.ExtraNotificationId))
                            ?? PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", "notification_id"));
                        if (notificationId == null) return false;

                        deepLink = PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", PusherHub.ExtraDeepLink))
                            ?? PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", "deep_link"));
                        tapId = PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", PusherHub.ExtraTapId))
                            ?? PusherHub.NullIfEmpty(intent.Call<string>("getStringExtra", "google.message_id"));

                        foreach (var key in new[] { PusherHub.ExtraNotificationId, "notification_id", PusherHub.ExtraDeepLink, "deep_link", PusherHub.ExtraTapId })
                        {
                            intent.Call("removeExtra", key);
                        }
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                PusherHub.Log("Couldn't read the launch Intent: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Posts a system notification for a push that arrived while the game
        /// was open, with a tap that comes back through TakeNotificationTap.
        /// Text only: the image URL isn't among the fields Firebase's Unity
        /// SDK passes on — pushes that arrive in the background (the usual
        /// case) do show their image, because Android draws those itself.
        /// </summary>
        internal static void ShowNotification(string title, string body, string notificationId, string deepLink)
        {
            // Required on Android 13+ — posting without it does nothing.
            if (SdkInt >= 33 && !Permission.HasUserAuthorizedPermission(PostNotifications)) return;

            try
            {
                using (var activity = CurrentActivity())
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var packageManager = context.Call<AndroidJavaObject>("getPackageManager"))
                {
                    var packageName = context.Call<string>("getPackageName");
                    var tapId = Guid.NewGuid().ToString();
                    // An untracked push (no notification_id) gets its own
                    // slot rather than replacing the previous one.
                    var requestCode = StableHash(notificationId ?? tapId);

                    using (var intent = packageManager.Call<AndroidJavaObject>("getLaunchIntentForPackage", packageName))
                    {
                        if (intent == null) return;
                        intent.Call<AndroidJavaObject>("setAction", PusherHub.ActionNotificationClick).Dispose();
                        intent.Call<AndroidJavaObject>("addFlags", IntentFlagActivityNewTask | IntentFlagActivitySingleTop).Dispose();
                        if (notificationId != null) intent.Call<AndroidJavaObject>("putExtra", PusherHub.ExtraNotificationId, notificationId).Dispose();
                        intent.Call<AndroidJavaObject>("putExtra", PusherHub.ExtraTapId, tapId).Dispose();
                        if (deepLink != null) intent.Call<AndroidJavaObject>("putExtra", PusherHub.ExtraDeepLink, deepLink).Dispose();

                        using (var pendingIntentClass = new AndroidJavaClass("android.app.PendingIntent"))
                        using (var pendingIntent = pendingIntentClass.CallStatic<AndroidJavaObject>("getActivity", context, requestCode, intent, PendingIntentFlagUpdateCurrent | PendingIntentFlagImmutable))
                        using (var builder = SdkInt >= 26
                            ? new AndroidJavaObject("android.app.Notification$Builder", context, PusherHub.ChannelId)
                            : new AndroidJavaObject("android.app.Notification$Builder", context))
                        {
                            if (title != null) builder.Call<AndroidJavaObject>("setContentTitle", title).Dispose();
                            if (body != null) builder.Call<AndroidJavaObject>("setContentText", body).Dispose();
                            builder.Call<AndroidJavaObject>("setAutoCancel", true).Dispose();
                            builder.Call<AndroidJavaObject>("setContentIntent", pendingIntent).Dispose();
                            builder.Call<AndroidJavaObject>("setSmallIcon", SmallIcon(context, packageManager, packageName)).Dispose();
                            if (SdkInt < 26) builder.Call<AndroidJavaObject>("setPriority", PriorityHigh).Dispose();

                            using (var notification = builder.Call<AndroidJavaObject>("build"))
                            using (var manager = NotificationManager(context))
                            {
                                manager.Call("notify", requestCode, notification);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                PusherHub.Log("Couldn't show the notification: " + e.Message);
            }
        }

        /// <summary>
        /// The same small icon Firebase uses for the notifications Android
        /// shows while the game is in the background: the
        /// com.google.firebase.messaging.default_notification_icon meta-data
        /// when the game sets one, its app icon otherwise.
        /// </summary>
        static int SmallIcon(AndroidJavaObject context, AndroidJavaObject packageManager, string packageName)
        {
            try
            {
                using (var appInfo = packageManager.Call<AndroidJavaObject>("getApplicationInfo", packageName, GetMetaData))
                {
                    using (var metaData = appInfo.Get<AndroidJavaObject>("metaData"))
                    {
                        var icon = metaData?.Call<int>("getInt", "com.google.firebase.messaging.default_notification_icon", 0) ?? 0;
                        if (icon != 0) return icon;
                    }
                    return appInfo.Get<int>("icon");
                }
            }
            catch (Exception)
            {
                using (var appInfo = context.Call<AndroidJavaObject>("getApplicationInfo")) return appInfo.Get<int>("icon");
            }
        }

        /// <summary>The same number for the same notification in every run — string.GetHashCode() isn't guaranteed to be.</summary>
        static int StableHash(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
            unchecked
            {
                var hash = 17;
                foreach (var c in value) hash = hash * 31 + c;
                return hash;
            }
        }
    }
#endif

    // ---------------------------------------------------------------------
    // In-app message model
    // ---------------------------------------------------------------------

    /// <summary>
    /// One in-app message returned by PusherHub.FetchInAppMessages() — a
    /// banner, modal, fullscreen, center-popup, or bottom-sheet message the
    /// backend decided this device is currently eligible to see.
    ///
    /// ContentMode picks how it's meant to be rendered: "simple" uses
    /// Heading/Body/Image/ButtonText (the fixed fields below); "blocks" uses
    /// Blocks, rendered in order; "html" uses HtmlContent, which needs a web
    /// view (PusherHub's own rendering skips these).
    /// </summary>
    public sealed class InAppMessage
    {
        public int Id { get; private set; }
        public string Type { get; private set; }
        public string ContentMode { get; private set; }
        public string Heading { get; private set; }
        public string Body { get; private set; }
        public List<InAppMessageBlock> Blocks { get; private set; }
        public string HtmlContent { get; private set; }
        public string Image { get; private set; }
        public string ButtonText { get; private set; }
        public string ButtonDeepLink { get; private set; }
        public string BackgroundColor { get; private set; }
        public string TextColor { get; private set; }
        public bool ShowCloseButton { get; private set; }
        public string CloseIconUrl { get; private set; }
        public float? CloseIconWidth { get; private set; }
        public float? CloseIconHeight { get; private set; }
        public string BackgroundImage { get; private set; }
        public float? Padding { get; private set; }
        public int? DismissAfterSeconds { get; private set; }
        public Dictionary<string, object> CustomData { get; private set; }

        internal static InAppMessage FromJson(Dictionary<string, object> json)
        {
            var id = PusherHubJson.Int(json, "id");
            var type = PusherHubJson.NonEmptyString(json, "type");
            if (id == null || type == null) return null;

            var blocks = new List<InAppMessageBlock>();
            if (json.TryGetValue("blocks", out var rawBlocks) && rawBlocks is List<object> blockList)
            {
                foreach (var block in blockList)
                {
                    if (block is Dictionary<string, object> blockJson) blocks.Add(InAppMessageBlock.FromJson(blockJson));
                }
            }

            return new InAppMessage
            {
                Id = id.Value,
                Type = type,
                ContentMode = PusherHubJson.NonEmptyString(json, "content_mode") ?? "simple",
                Heading = PusherHubJson.NonEmptyString(json, "heading"),
                Body = PusherHubJson.NonEmptyString(json, "body"),
                Blocks = blocks,
                HtmlContent = PusherHubJson.NonEmptyString(json, "html_content"),
                Image = PusherHubJson.NonEmptyString(json, "image"),
                ButtonText = PusherHubJson.NonEmptyString(json, "button_text"),
                ButtonDeepLink = PusherHubJson.NonEmptyString(json, "button_deep_link"),
                BackgroundColor = PusherHubJson.NonEmptyString(json, "background_color"),
                TextColor = PusherHubJson.NonEmptyString(json, "text_color"),
                ShowCloseButton = PusherHubJson.Bool(json, "show_close_button") ?? true,
                CloseIconUrl = PusherHubJson.NonEmptyString(json, "close_icon_url"),
                CloseIconWidth = PusherHubJson.Float(json, "close_icon_width"),
                CloseIconHeight = PusherHubJson.Float(json, "close_icon_height"),
                BackgroundImage = PusherHubJson.NonEmptyString(json, "background_image"),
                Padding = PusherHubJson.Float(json, "padding"),
                DismissAfterSeconds = PusherHubJson.Int(json, "dismiss_after_seconds"),
                CustomData = json.TryGetValue("custom_data", out var customData) ? customData as Dictionary<string, object> : null,
            };
        }
    }

    /// <summary>One element of a "blocks" in-app message: a text, image, or button.</summary>
    public sealed class InAppMessageBlock
    {
        public string Type { get; private set; }
        public string Text { get; private set; }
        public string Color { get; private set; }
        public float? FontSize { get; private set; }
        public bool Bold { get; private set; }
        public bool Italic { get; private set; }
        public bool Underline { get; private set; }
        public string Align { get; private set; }
        public string Image { get; private set; }
        public string BackgroundColor { get; private set; }
        public string FontColor { get; private set; }
        public float? CornerRadius { get; private set; }

        /// <summary>"open_url", "deep_link", or "close" — null when tapping does nothing.</summary>
        public string ClickActionType { get; private set; }
        public string ClickActionValue { get; private set; }

        internal static InAppMessageBlock FromJson(Dictionary<string, object> json)
        {
            var clickAction = json.TryGetValue("click_action", out var rawAction) ? rawAction as Dictionary<string, object> : null;
            return new InAppMessageBlock
            {
                Type = PusherHubJson.NonEmptyString(json, "type") ?? "",
                Text = PusherHubJson.String(json, "text"),
                Color = PusherHubJson.NonEmptyString(json, "color"),
                FontSize = PusherHubJson.Float(json, "font_size"),
                Bold = PusherHubJson.Bool(json, "bold") ?? false,
                Italic = PusherHubJson.Bool(json, "italic") ?? false,
                Underline = PusherHubJson.Bool(json, "underline") ?? false,
                Align = PusherHubJson.NonEmptyString(json, "align"),
                Image = PusherHubJson.NonEmptyString(json, "image"),
                BackgroundColor = PusherHubJson.NonEmptyString(json, "background_color"),
                FontColor = PusherHubJson.NonEmptyString(json, "font_color"),
                CornerRadius = PusherHubJson.Float(json, "corner_radius"),
                ClickActionType = clickAction == null ? null : PusherHubJson.NonEmptyString(clickAction, "type"),
                ClickActionValue = clickAction == null ? null : PusherHubJson.NonEmptyString(clickAction, "value"),
            };
        }
    }

    // ---------------------------------------------------------------------
    // In-app message rendering
    // ---------------------------------------------------------------------

    internal enum InAppOutcome
    {
        /// <summary>✕, a "close" action, or a button with no link.</summary>
        Closed,
        /// <summary>Tapped outside, or timed out — closed without being acted on.</summary>
        Dismissed,
        Opened,
    }

    /// <summary>
    /// Shows one message on its own overlay Canvas above the whole game, so it
    /// never interferes with the game's own UI — the Unity equivalent of the
    /// Android SDKs' Dialog. Built with Unity UI only, from code: no prefab,
    /// font asset, or sprite to import. Sizes are in dp, like Android's.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class PusherHubInAppView : MonoBehaviour
    {
        static readonly Color DefaultTextColor = new Color32(15, 23, 42, 255);
        static readonly Color DefaultBlockButtonColor = new Color32(5, 27, 44, 255);
        // The dashboard preview's simple-mode button (Tailwind emerald-600).
        static readonly Color SimpleButtonColor = new Color32(5, 150, 105, 255);

        InAppMessage message;
        Action<InAppOutcome, string> onFinish;
        CanvasGroup group;
        RectTransform card;
        GameObject createdEventSystem;
        bool finished;
        float scale;
        float contentWidth;
        Color textColor;
        Color cardColor;
        readonly List<UnityEngine.Object> loaded = new List<UnityEngine.Object>();

        internal static PusherHubInAppView Show(InAppMessage message, Action<InAppOutcome, string> onFinish)
        {
            var root = new GameObject("PusherHub In-App Message", typeof(RectTransform));
            DontDestroyOnLoad(root);
            var view = root.AddComponent<PusherHubInAppView>();
            view.message = message;
            view.onFinish = onFinish;
            view.Build();
            return view;
        }

        void Build()
        {
            scale = PusherHubScreen.Scale();
            textColor = PusherHubColors.Parse(message.TextColor) ?? DefaultTextColor;
            cardColor = PusherHubColors.Parse(message.BackgroundColor) ?? Color.white;
            var isFullscreen = message.Type == "fullscreen";

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = scale;
            gameObject.AddComponent<GraphicRaycaster>();
            group = gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0;

            // Taps need an EventSystem; borrow the scene's, or bring one.
            if (EventSystem.current == null) createdEventSystem = PusherHubScreen.CreateEventSystem();

            // Tapping outside the message dismisses it, like the Android SDKs'
            // dialogs — except fullscreen, which has no outside. A banner
            // leaves the game undimmed.
            var backdrop = Child("Backdrop", transform);
            Stretch(backdrop);
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = isFullscreen ? cardColor : message.Type == "banner" ? Color.clear : new Color(0, 0, 0, 0.4f);
            if (!isFullscreen) OnTap(backdrop.gameObject, () => Finish(InAppOutcome.Dismissed, null));

            var safeArea = Child("SafeArea", transform);
            var safe = Screen.safeArea;
            safeArea.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            safeArea.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            safeArea.offsetMin = safeArea.offsetMax = Vector2.zero;
            var safeWidth = safe.width / scale;

            card = Child("Card", safeArea);
            var padding = message.Padding ?? 16f;
            float cardWidth;
            if (isFullscreen)
            {
                Stretch(card);
                cardWidth = safeWidth;
            }
            else
            {
                var isEdge = message.Type == "banner" || message.Type == "bottom_sheet";
                // Capped so a landscape screen gets a card, not a strip
                // across the whole display.
                cardWidth = Mathf.Min(safeWidth - (isEdge ? 24f : 48f), isEdge ? 600f : 420f);
                var anchorY = message.Type == "banner" ? 1f : message.Type == "bottom_sheet" ? 0f : 0.5f;
                card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, anchorY);
                card.anchoredPosition = new Vector2(0, message.Type == "banner" ? -8f : message.Type == "bottom_sheet" ? 8f : 0f);
                card.sizeDelta = new Vector2(cardWidth, 0);
                var fitter = card.gameObject.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            contentWidth = cardWidth - padding * 2;

            var cardImage = card.gameObject.AddComponent<Image>();
            cardImage.color = isFullscreen ? Color.clear : cardColor;
            if (!isFullscreen)
            {
                cardImage.sprite = PusherHubSprites.Rounded(16, scale);
                cardImage.type = Image.Type.Sliced;
                // Clips the background image and content to the rounded corners.
                card.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            }

            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            var pad = Mathf.RoundToInt(padding);
            layout.padding = new RectOffset(pad, pad, pad, pad);
            layout.spacing = 8;
            layout.childAlignment = isFullscreen ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            if (message.BackgroundImage != null) AddBackgroundImage(message.BackgroundImage);

            if (message.ContentMode == "blocks")
            {
                foreach (var block in message.Blocks) AddBlock(block);
            }
            else
            {
                AddSimpleContent(isFullscreen);
            }

            if (message.ShowCloseButton) AddCloseButton(isFullscreen ? safeArea : card);

            StartCoroutine(Fade(1f, null));
            if (message.DismissAfterSeconds is int seconds && seconds > 0)
            {
                StartCoroutine(DismissAfter(seconds));
            }
        }

        void AddSimpleContent(bool isFullscreen)
        {
            var alignment = isFullscreen ? TextAnchor.UpperCenter : TextAnchor.UpperLeft;

            if (message.Image != null) AddRemoteImage(message.Image, card, null);
            if (message.Heading != null)
            {
                // Keeps a long heading clear of the ✕ drawn over its corner.
                var rightInset = message.ShowCloseButton && !isFullscreen ? 20 : 0;
                AddText(message.Heading, 18, FontStyle.Bold, textColor, alignment, rightInset);
            }
            if (message.Body != null)
            {
                var bodyColor = textColor;
                bodyColor.a *= 0.85f;
                AddText(message.Body, 14, FontStyle.Normal, bodyColor, alignment, 0);
            }
            if (message.ButtonText != null)
            {
                AddButton(message.ButtonText, Color.white, SimpleButtonColor, 8, () =>
                {
                    if (message.ButtonDeepLink != null) Finish(InAppOutcome.Opened, message.ButtonDeepLink);
                    else Finish(InAppOutcome.Closed, null);
                });
            }
        }

        void AddBlock(InAppMessageBlock block)
        {
            switch (block.Type)
            {
                case "text":
                    var style = block.Bold && block.Italic ? FontStyle.BoldAndItalic : block.Bold ? FontStyle.Bold : block.Italic ? FontStyle.Italic : FontStyle.Normal;
                    // Blocks default to centered, matching the dashboard
                    // preview and the other SDKs. Unity's built-in text has no
                    // underline, so an underlined block shows without one.
                    var alignment = block.Align == "left" ? TextAnchor.UpperLeft : block.Align == "right" ? TextAnchor.UpperRight : TextAnchor.UpperCenter;
                    var text = AddText(block.Text ?? "", Mathf.RoundToInt(block.FontSize ?? 16), style, PusherHubColors.Parse(block.Color) ?? textColor, alignment, 0);
                    if (block.ClickActionType != null) OnTap(text, () => HandleBlockTap(block));
                    break;
                case "image":
                    if (block.Image != null) AddRemoteImage(block.Image, card, block.ClickActionType != null ? () => HandleBlockTap(block) : (Action)null);
                    break;
                case "button":
                    AddButton(
                        block.Text ?? "Button",
                        PusherHubColors.Parse(block.FontColor) ?? Color.white,
                        PusherHubColors.Parse(block.BackgroundColor) ?? DefaultBlockButtonColor,
                        block.CornerRadius ?? 8,
                        () => HandleBlockTap(block));
                    break;
            }
        }

        void HandleBlockTap(InAppMessageBlock block)
        {
            switch (block.ClickActionType)
            {
                case "open_url":
                case "deep_link":
                    if (block.ClickActionValue != null) Finish(InAppOutcome.Opened, block.ClickActionValue);
                    break;
                case "close":
                    Finish(InAppOutcome.Closed, null);
                    break;
            }
        }

        GameObject AddText(string value, int fontSize, FontStyle style, Color color, TextAnchor alignment, int rightInset)
        {
            var holder = Child("Text", card);
            var holderLayout = holder.gameObject.AddComponent<VerticalLayoutGroup>();
            holderLayout.padding = new RectOffset(0, rightInset, 0, 0);
            holderLayout.childControlWidth = holderLayout.childControlHeight = true;
            holderLayout.childForceExpandWidth = true;
            holderLayout.childForceExpandHeight = false;

            var label = Child("Label", holder).gameObject.AddComponent<Text>();
            label.font = PusherHubScreen.Font;
            label.text = value;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            // Admin-authored text is shown as written, never parsed as markup.
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return holder.gameObject;
        }

        void AddButton(string label, Color labelColor, Color background, float cornerRadius, Action onClick)
        {
            var button = Child("Button", card);
            var element = button.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 44;
            var image = button.gameObject.AddComponent<Image>();
            image.sprite = PusherHubSprites.Rounded(Mathf.Max(1, Mathf.RoundToInt(cornerRadius)), scale);
            image.type = Image.Type.Sliced;
            image.color = background;
            OnTap(button.gameObject, onClick);

            var text = Child("Label", button);
            Stretch(text);
            var labelText = text.gameObject.AddComponent<Text>();
            labelText.font = PusherHubScreen.Font;
            labelText.text = label;
            labelText.fontSize = 16;
            labelText.fontStyle = FontStyle.Bold;
            labelText.color = labelColor;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.supportRichText = false;
            labelText.raycastTarget = false;
        }

        /// <summary>
        /// The whole image at its natural aspect ratio — no crop, no
        /// letterboxing — matching the dashboard preview's &lt;img class="w-full"&gt;.
        /// Takes no space until it has loaded.
        /// </summary>
        void AddRemoteImage(string url, RectTransform parent, Action onClick)
        {
            var holder = Child("Image", parent);
            var element = holder.gameObject.AddComponent<LayoutElement>();
            var image = holder.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            if (onClick != null) OnTap(holder.gameObject, onClick);
            else image.raycastTarget = false;
            // Inactive, the layout skips it — spacing included.
            holder.gameObject.SetActive(false);

            StartCoroutine(LoadSprite(url, sprite =>
            {
                image.sprite = sprite;
                element.preferredHeight = contentWidth * sprite.rect.height / sprite.rect.width;
                holder.gameObject.SetActive(true);
                LayoutRebuilder.MarkLayoutForRebuild(card);
            }));
        }

        /// <summary>
        /// Painted behind the content rather than taking up space in the flow,
        /// so the card stays the size of its content — like the preview's CSS
        /// background-image.
        /// </summary>
        void AddBackgroundImage(string url)
        {
            var holder = Child("BackgroundImage", card);
            holder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            holder.anchorMin = holder.anchorMax = holder.pivot = new Vector2(0.5f, 0.5f);
            var image = holder.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.enabled = false;
            var fitter = holder.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;

            StartCoroutine(LoadSprite(url, sprite =>
            {
                image.sprite = sprite;
                image.enabled = true;
                fitter.aspectRatio = sprite.rect.width / sprite.rect.height;
            }));
        }

        void AddCloseButton(RectTransform parent)
        {
            var close = Child("Close", parent);
            close.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            close.anchorMin = close.anchorMax = close.pivot = Vector2.one;
            close.anchoredPosition = Vector2.zero;
            close.sizeDelta = new Vector2(44, 44);
            // A transparent hit area the size of a fingertip around a small icon.
            close.gameObject.AddComponent<Image>().color = Color.clear;
            OnTap(close.gameObject, () => Finish(InAppOutcome.Closed, null));

            if (message.CloseIconUrl != null)
            {
                var icon = Child("Icon", close);
                icon.sizeDelta = new Vector2(message.CloseIconWidth ?? 18, message.CloseIconHeight ?? 18);
                var image = icon.gameObject.AddComponent<Image>();
                image.preserveAspect = true;
                image.raycastTarget = false;
                image.enabled = false;
                StartCoroutine(LoadSprite(message.CloseIconUrl, sprite =>
                {
                    image.sprite = sprite;
                    image.enabled = true;
                }));
                return;
            }

            // Its own small disc, so it stays legible where it sits over an
            // image rather than the card's color.
            var disc = Child("Disc", close);
            disc.sizeDelta = new Vector2(28, 28);
            var discImage = disc.gameObject.AddComponent<Image>();
            discImage.sprite = PusherHubSprites.Rounded(14, scale);
            discImage.type = Image.Type.Sliced;
            var discColor = cardColor;
            discColor.a *= 0.85f;
            discImage.color = discColor;
            discImage.raycastTarget = false;

            foreach (var angle in new[] { 45f, -45f })
            {
                var bar = Child("Stroke", disc);
                bar.sizeDelta = new Vector2(13, 2);
                bar.localRotation = Quaternion.Euler(0, 0, angle);
                var barImage = bar.gameObject.AddComponent<Image>();
                barImage.color = textColor;
                barImage.raycastTarget = false;
            }
        }

        IEnumerator LoadSprite(string url, Action<Sprite> onLoaded)
        {
            using (var request = UnityWebRequestTexture.GetTexture(url))
            {
                request.timeout = 15;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    PusherHub.Log("Failed to load image " + url + ": " + request.error);
                    yield break;
                }
                var texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null || texture.width == 0 || texture.height == 0) yield break;
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100);
                loaded.Add(texture);
                loaded.Add(sprite);
                onLoaded(sprite);
            }
        }

        IEnumerator DismissAfter(int seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Finish(InAppOutcome.Dismissed, null);
        }

        IEnumerator Fade(float to, Action done)
        {
            var from = group.alpha;
            for (var t = 0f; t < 0.2f; t += Time.unscaledDeltaTime)
            {
                group.alpha = Mathf.Lerp(from, to, t / 0.2f);
                yield return null;
            }
            group.alpha = to;
            done?.Invoke();
        }

        void Finish(InAppOutcome outcome, string link)
        {
            if (finished) return;
            finished = true;
            group.interactable = group.blocksRaycasts = false;
            StopAllCoroutines();
            StartCoroutine(Fade(0f, () => Destroy(gameObject)));
            onFinish(outcome, link);
        }

        void OnDestroy()
        {
            if (createdEventSystem != null) Destroy(createdEventSystem);
            foreach (var item in loaded)
            {
                if (item != null) Destroy(item);
            }
        }

        static RectTransform Child(string name, Transform parent)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            return (RectTransform)child.transform;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        static void OnTap(GameObject target, Action action)
        {
            var graphic = target.GetComponent<Graphic>();
            if (graphic == null)
            {
                // A Text holder has no graphic of its own — make it hittable.
                graphic = target.AddComponent<Image>();
                graphic.color = Color.clear;
            }
            graphic.raycastTarget = true;
            var button = target.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            var navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            button.onClick.AddListener(() => action());
        }
    }

    internal static class PusherHubScreen
    {
        static Font font;

        /// <summary>Unity's built-in font — so the SDK ships no font asset of its own.</summary>
        internal static Font Font
        {
            get
            {
                if (font == null)
                {
#if UNITY_2022_2_OR_NEWER
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
                    font = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
                }
                return font;
            }
        }

        /// <summary>
        /// Screen pixels per dp, measured against what Unity actually renders
        /// — a game that renders below the display's native resolution still
        /// gets messages the size the other SDKs draw them.
        /// </summary>
        internal static float Scale()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var resources = activity.Call<AndroidJavaObject>("getResources"))
                using (var metrics = resources.Call<AndroidJavaObject>("getDisplayMetrics"))
                {
                    var density = metrics.Get<float>("density");
                    var nativeShortSide = Mathf.Min(metrics.Get<int>("widthPixels"), metrics.Get<int>("heightPixels"));
                    var renderedShortSide = Mathf.Min(Screen.width, Screen.height);
                    if (density > 0 && nativeShortSide > 0) return density * renderedShortSide / nativeShortSide;
                }
            }
            catch (Exception e)
            {
                PusherHub.Log("Couldn't read the screen density: " + e.Message);
            }
#endif
            return Screen.dpi > 0 ? Mathf.Max(1f, Screen.dpi / 160f) : 1f;
        }

        internal static GameObject CreateEventSystem()
        {
            var eventSystem = new GameObject("PusherHub EventSystem", typeof(EventSystem));
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
#if ENABLE_LEGACY_INPUT_MANAGER
            eventSystem.AddComponent<StandaloneInputModule>();
#else
            // A project on the Input System package only — its UI module,
            // found by name so this file still compiles without that package.
            var module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null) eventSystem.AddComponent(module);
#endif
            return eventSystem;
        }
    }

    internal static class PusherHubSprites
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        /// <summary>
        /// A white rounded rectangle to 9-slice: radius in dp, drawn at the
        /// screen's own pixel density so the corners stay crisp. Tinted by
        /// the Image's color.
        /// </summary>
        internal static Sprite Rounded(int radiusDp, float scale)
        {
            var key = radiusDp + "@" + scale.ToString("0.00", CultureInfo.InvariantCulture);
            if (cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var radius = Mathf.Max(1, Mathf.CeilToInt(radiusDp * scale));
            var size = radius * 2 + 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // How far this pixel's center lies outside the 2×2 middle
                    // square: within the radius it's inside the shape, and the
                    // last half pixel fades out to smooth the edge.
                    var dx = Mathf.Max(0f, Mathf.Max(radius - (x + 0.5f), x + 0.5f - (size - radius)));
                    var dy = Mathf.Max(0f, Mathf.Max(radius - (y + 0.5f), y + 0.5f - (size - radius)));
                    var alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var border = radius;
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f * scale, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.hideFlags = HideFlags.DontSave;
            cache[key] = sprite;
            return sprite;
        }
    }

    internal static class PusherHubColors
    {
        /// <summary>
        /// "#RRGGBB" or "#AARRGGBB" (Android's order, which the dashboard's
        /// colors follow); null for anything else, so callers fall back to a
        /// default.
        /// </summary>
        internal static Color? Parse(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var digits = hex.Trim().TrimStart('#');
            if ((digits.Length != 6 && digits.Length != 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;

            var alpha = digits.Length == 8 ? (byte)((value >> 24) & 0xFF) : (byte)255;
            return new Color32((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF), alpha);
        }
    }

    /// <summary>
    /// A small JSON reader/writer, so the SDK doesn't depend on a JSON package
    /// the game may not have. Objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double (or long when whole).
    /// </summary>
    internal static class PusherHubJson
    {
        internal static string Serialize(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        static void Write(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case string text:
                    WriteString(builder, text);
                    break;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    break;
                case int _:
                case long _:
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case float _:
                case double _:
                    builder.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> map:
                    builder.Append('{');
                    var first = true;
                    foreach (var pair in map)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        WriteString(builder, pair.Key);
                        builder.Append(':');
                        Write(builder, pair.Value);
                    }
                    builder.Append('}');
                    break;
                case IEnumerable<object> list:
                    builder.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) builder.Append(',');
                        firstItem = false;
                        Write(builder, item);
                    }
                    builder.Append(']');
                    break;
                default:
                    WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        /// <summary>The parsed value, or null when the text isn't valid JSON.</summary>
        internal static object Deserialize(string json)
        {
            try
            {
                var index = 0;
                var value = ReadValue(json, ref index);
                SkipWhitespace(json, ref index);
                return index == json.Length ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static object ReadValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) throw new FormatException();
            var c = json[index];
            if (c == '{') return ReadObject(json, ref index);
            if (c == '[') return ReadArray(json, ref index);
            if (c == '"') return ReadString(json, ref index);
            if (Matches(json, ref index, "true")) return true;
            if (Matches(json, ref index, "false")) return false;
            if (Matches(json, ref index, "null")) return null;
            return ReadNumber(json, ref index);
        }

        static Dictionary<string, object> ReadObject(string json, ref int index)
        {
            var map = new Dictionary<string, object>();
            index++;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}')
            {
                index++;
                return map;
            }
            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"') throw new FormatException();
                var key = ReadString(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') throw new FormatException();
                index++;
                map[key] = ReadValue(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length) throw new FormatException();
                if (json[index] == ',') { index++; continue; }
                if (json[index] == '}') { index++; return map; }
                throw new FormatException();
            }
        }

        static List<object> ReadArray(string json, ref int index)
        {
            var list = new List<object>();
            index++;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']')
            {
                index++;
                return list;
            }
            while (true)
            {
                list.Add(ReadValue(json, ref index));
                SkipWhitespace(json, ref index);
                if (index >= json.Length) throw new FormatException();
                if (json[index] == ',') { index++; continue; }
                if (json[index] == ']') { index++; return list; }
                throw new FormatException();
            }
        }

        static string ReadString(string json, ref int index)
        {
            var builder = new StringBuilder();
            index++;
            while (index < json.Length)
            {
                var c = json[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (index >= json.Length) break;
                var escape = json[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length) throw new FormatException();
                        builder.Append((char)int.Parse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default: throw new FormatException();
                }
            }
            throw new FormatException();
        }

        static object ReadNumber(string json, ref int index)
        {
            var start = index;
            while (index < json.Length && "+-0123456789.eE".IndexOf(json[index]) >= 0) index++;
            var text = json.Substring(start, index - start);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            throw new FormatException();
        }

        static bool Matches(string json, ref int index, string literal)
        {
            if (string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0) return false;
            index += literal.Length;
            return true;
        }

        static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        }

        // Field values come from admin-authored JSON and FCM payloads —
        // tolerate a number arriving as a string (or the reverse) rather than
        // dropping it.

        internal static string String(Dictionary<string, object> json, string key)
        {
            if (!json.TryGetValue(key, out var value) || value == null) return null;
            return value is string text ? text : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static string NonEmptyString(Dictionary<string, object> json, string key)
        {
            var value = String(json, key);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        internal static float? Float(Dictionary<string, object> json, string key)
        {
            if (!json.TryGetValue(key, out var value) || value == null) return null;
            switch (value)
            {
                case long whole: return whole;
                case double number: return (float)number;
                case string text when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed): return parsed;
                default: return null;
            }
        }

        internal static int? Int(Dictionary<string, object> json, string key)
        {
            var value = Float(json, key);
            return value.HasValue ? (int?)Mathf.RoundToInt(value.Value) : null;
        }

        internal static bool? Bool(Dictionary<string, object> json, string key)
        {
            if (!json.TryGetValue(key, out var value) || value == null) return null;
            switch (value)
            {
                case bool flag: return flag;
                case long whole: return whole != 0;
                case double number: return number != 0;
                case string text: return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
                default: return null;
            }
        }
    }
}

#if UNITY_EDITOR && UNITY_ANDROID
namespace PusherHubSDK.EditorSupport
{
    using System.IO;
    using System.Xml;
    using UnityEditor.Android;

    /// <summary>
    /// Adds an intent-filter for PusherHub.ActionNotificationClick to the
    /// game's launcher Activity in every Android build. PusherHub's backend
    /// sets that action as every push's click_action, and Android only
    /// delivers a background notification's tap to an Activity that declares
    /// it — without this, tapping such a notification does nothing at all.
    /// Runs in the Editor only; nothing of it ships in the game.
    /// </summary>
    public sealed class PusherHubAndroidManifestPatcher : IPostGenerateGradleAndroidProject
    {
        const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            // The launcher Activity lives in unityLibrary's manifest; the
            // launcher module's is checked too, for projects that move it.
            var manifests = new[]
            {
                Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml"),
                Path.Combine(unityLibraryPath, "..", "launcher", "src", "main", "AndroidManifest.xml"),
            };

            foreach (var path in manifests)
            {
                if (!File.Exists(path)) continue;

                var document = new XmlDocument();
                document.Load(path);
                var namespaces = new XmlNamespaceManager(document.NameTable);
                namespaces.AddNamespace("android", AndroidNamespace);

                var activities = document.SelectNodes("/manifest/application/activity[intent-filter/category/@android:name='android.intent.category.LAUNCHER']", namespaces);
                if (activities == null) continue;

                var changed = false;
                foreach (XmlElement activity in activities)
                {
                    if (activity.SelectSingleNode("intent-filter/action[@android:name='" + PusherHub.ActionNotificationClick + "']", namespaces) != null) continue;

                    var filter = document.CreateElement("intent-filter");
                    filter.AppendChild(Element(document, "action", PusherHub.ActionNotificationClick));
                    filter.AppendChild(Element(document, "category", "android.intent.category.DEFAULT"));
                    activity.AppendChild(filter);
                    changed = true;
                }

                if (changed) document.Save(path);
            }
        }

        static XmlElement Element(XmlDocument document, string name, string androidName)
        {
            var element = document.CreateElement(name);
            var attribute = document.CreateAttribute("android", "name", AndroidNamespace);
            attribute.Value = androidName;
            element.Attributes.Append(attribute);
            return element;
        }
    }
}
#endif

#if UNITY_EDITOR && UNITY_IOS
namespace PusherHubSDK.EditorSupport
{
    using System.IO;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Build.Reporting;
    using UnityEditor.iOS.Xcode;
    using UnityEditor.iOS.Xcode.Extensions;

    /// <summary>
    /// Readies every iOS build's Xcode project for PusherHub, so there's
    /// nothing to set up in Xcode by hand: the Push Notifications capability
    /// and the remote-notification background mode, plus a Notification
    /// Service Extension — iOS never downloads a push's picture by itself.
    /// A game that already has an extension (OneSignal adds one) gets
    /// PusherHub's picture download added to it rather than a second one,
    /// since iOS runs only one. Runs in the Editor only.
    /// </summary>
    public sealed class PusherHubXcodeProjectPatcher : IPostprocessBuildWithReport
    {
        const string ExtensionName = "PusherHubNotificationService";
        const string SourceFileName = "NotificationService.swift";
        const string PatchedMarker = "PusherHub picture download";

        // After OneSignal's own post-processor (45), so its extension exists by now.
        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;

            var output = report.summary.outputPath;
            var projectPath = PBXProject.GetPBXProjectPath(output);

            AddPushCapability(output, projectPath);

            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            var existing = FindServiceExtension(project, output);
            if (existing != null) AddPicturesToExisting(existing);
            else AddOwnExtension(project, output);
            project.WriteToFile(projectPath);
        }

        static void AddPushCapability(string output, string projectPath)
        {
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            var target = project.GetUnityMainTargetGuid();

            var entitlements = project.GetBuildPropertyForAnyConfig(target, "CODE_SIGN_ENTITLEMENTS");
            if (string.IsNullOrEmpty(entitlements) || !File.Exists(Path.Combine(output, entitlements)))
            {
                entitlements = "Unity-iPhone/Unity-iPhone.entitlements";
            }

            var document = new PlistDocument();
            var entitlementsPath = Path.Combine(output, entitlements);
            if (File.Exists(entitlementsPath)) document.ReadFromFile(entitlementsPath);

            // Left alone when another plugin (OneSignal) already set it.
            if (document.root["aps-environment"] == null)
            {
                var capabilities = new ProjectCapabilityManager(projectPath, entitlements, null, target);
                capabilities.AddPushNotifications(EditorUserBuildSettings.development);
                capabilities.WriteToFile();
            }

            var infoPath = Path.Combine(output, "Info.plist");
            var info = new PlistDocument();
            info.ReadFromFile(infoPath);
            var modes = info.root["UIBackgroundModes"] as PlistElementArray ?? info.root.CreateArray("UIBackgroundModes");
            if (!modes.values.Exists(mode => mode is PlistElementString text && text.value == "remote-notification"))
            {
                modes.AddString("remote-notification");
                info.WriteToFile(infoPath);
            }
        }

        /// <summary>The source file of a Notification Service Extension already in the project, if any.</summary>
        static string FindServiceExtension(PBXProject project, string output)
        {
            foreach (var directory in Directory.GetDirectories(output))
            {
                var plist = Path.Combine(directory, "Info.plist");
                var source = Path.Combine(directory, SourceFileName);
                if (!File.Exists(plist) || !File.Exists(source)) continue;
                if (!File.ReadAllText(plist).Contains("com.apple.usernotifications.service")) continue;
                if (project.TargetGuidByName(Path.GetFileName(directory)) == null) continue;
                return source;
            }
            return null;
        }

        /// <summary>
        /// Pushes from PusherHub arrive through Firebase, which puts the
        /// picture in fcm_options.image; those get PusherHub's download, and
        /// every other push goes to the extension's own code exactly as before.
        /// </summary>
        static void AddPicturesToExisting(string sourcePath)
        {
            var source = File.ReadAllText(sourcePath);
            if (source.Contains(PatchedMarker)) return;

            const string classLine = "class NotificationService: UNNotificationServiceExtension {";
            const string didReceive = "withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) {";
            const string willExpire = "override func serviceExtensionTimeWillExpire() {";
            if (!source.Contains(classLine) || !source.Contains(didReceive) || !source.Contains(willExpire))
            {
                Debug.LogWarning("PusherHub: couldn't add picture support to " + sourcePath + " — PusherHub pushes will show without their picture.");
                return;
            }

            source = "import UniformTypeIdentifiers\n" + source
                .Replace(classLine, classLine + "\n    // " + PatchedMarker + " — added by PusherHub.cs at build time.\n    private let pusherHubPictures = PusherHubPictures()\n")
                .Replace(didReceive, didReceive + "\n        if pusherHubPictures.handle(request, withContentHandler: contentHandler) { return }\n")
                .Replace(willExpire, willExpire + "\n        pusherHubPictures.expire()\n")
                + "\n" + PicturesSource;
            File.WriteAllText(sourcePath, source);
        }

        static void AddOwnExtension(PBXProject project, string output)
        {
            var mainTarget = project.GetUnityMainTargetGuid();
            var directory = Path.Combine(output, ExtensionName);
            Directory.CreateDirectory(directory);

            var plist = new PlistDocument();
            plist.root.SetString("CFBundleDevelopmentRegion", "$(DEVELOPMENT_LANGUAGE)");
            plist.root.SetString("CFBundleDisplayName", ExtensionName);
            plist.root.SetString("CFBundleExecutable", "$(EXECUTABLE_NAME)");
            plist.root.SetString("CFBundleIdentifier", "$(PRODUCT_BUNDLE_IDENTIFIER)");
            plist.root.SetString("CFBundleInfoDictionaryVersion", "6.0");
            plist.root.SetString("CFBundleName", "$(PRODUCT_NAME)");
            plist.root.SetString("CFBundlePackageType", "$(PRODUCT_BUNDLE_PACKAGE_TYPE)");
            // Must match the game's, or App Store uploads are refused.
            plist.root.SetString("CFBundleShortVersionString", PlayerSettings.bundleVersion);
            plist.root.SetString("CFBundleVersion", PlayerSettings.iOS.buildNumber);
            var extension = plist.root.CreateDict("NSExtension");
            extension.SetString("NSExtensionPointIdentifier", "com.apple.usernotifications.service");
            extension.SetString("NSExtensionPrincipalClass", "$(PRODUCT_MODULE_NAME).NotificationService");
            plist.WriteToFile(Path.Combine(directory, "Info.plist"));

            File.WriteAllText(Path.Combine(directory, SourceFileName), ExtensionSource + "\n" + PicturesSource);

#if UNITY_2021_2_OR_NEWER
            var bundleId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS);
#else
            var bundleId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS);
#endif
            var target = project.AddAppExtension(mainTarget, ExtensionName, bundleId + "." + ExtensionName, ExtensionName + "/Info.plist");
            var sources = project.AddSourcesBuildPhase(target);
            project.AddFileToBuildSection(target, sources, project.AddFile(ExtensionName + "/" + SourceFileName, ExtensionName + "/" + SourceFileName));
            project.AddFile(ExtensionName + "/Info.plist", ExtensionName + "/Info.plist");

            project.SetBuildProperty(target, "TARGETED_DEVICE_FAMILY", "1,2");
            // The game's own minimum: Xcode would otherwise pick the newest
            // iOS, and an iPhone on anything older silently skips the extension.
            project.SetBuildProperty(target, "IPHONEOS_DEPLOYMENT_TARGET", PlayerSettings.iOS.targetOSVersionString);
            project.SetBuildProperty(target, "SWIFT_VERSION", "5.0");
            project.SetBuildProperty(target, "ARCHS", "arm64");
            project.SetBuildProperty(target, "DEVELOPMENT_TEAM", PlayerSettings.iOS.appleDeveloperTeamID);
            project.SetBuildProperty(target, "CODE_SIGN_STYLE", PlayerSettings.iOS.appleEnableAutomaticSigning ? "Automatic" : "Manual");
        }

        const string ExtensionSource = @"// NotificationService.swift: shows PusherHub notification pictures on iOS.
// Written by PusherHub.cs at build time — edits here are overwritten.

import UniformTypeIdentifiers
import UserNotifications

class NotificationService: UNNotificationServiceExtension {
    private let pusherHubPictures = PusherHubPictures()

    override func didReceive(_ request: UNNotificationRequest, withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) {
        if !pusherHubPictures.handle(request, withContentHandler: contentHandler) {
            contentHandler(request.content)
        }
    }

    // iOS gives an extension about 30 seconds; past that, the text alone.
    override func serviceExtensionTimeWillExpire() {
        pusherHubPictures.expire()
    }
}
";

        // The picture download itself, shared by PusherHub's own extension and
        // the copy added to another SDK's. Delivers the push exactly once,
        // with its picture or — on any failure or timeout — without it.
        const string PicturesSource = @"final class PusherHubPictures: @unchecked Sendable {
    // The download finishes on a URLSession queue while iOS may call
    // expire() on another, so state is only touched under the lock.
    private let lock = NSLock()
    private var handler: ((UNNotificationContent) -> Void)?
    private var content: UNMutableNotificationContent?
    private var task: URLSessionDownloadTask?

    /// True when this is a push with a picture from Firebase (fcm_options.image)
    /// and the download has started; false leaves the push to the caller.
    func handle(_ request: UNNotificationRequest, withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) -> Bool {
        guard let options = request.content.userInfo[""fcm_options""] as? [String: Any],
              let string = options[""image""] as? String,
              let url = URL(string: string),
              url.scheme == ""https"" || url.scheme == ""http"",
              let content = request.content.mutableCopy() as? UNMutableNotificationContent
        else { return false }

        let task = URLSession.shared.downloadTask(with: url) { [self] file, response, _ in
            finish(attaching: file.flatMap { Self.attachment(from: $0, response: response, url: url) })
        }
        lock.lock()
        handler = contentHandler
        self.content = content
        self.task = task
        lock.unlock()
        task.resume()
        return true
    }

    func expire() {
        lock.lock()
        let running = task
        lock.unlock()
        running?.cancel()
        finish(attaching: nil)
    }

    private func finish(attaching attachment: UNNotificationAttachment?) {
        lock.lock()
        guard let handler, let content else {
            lock.unlock()
            return
        }
        self.handler = nil
        if let attachment { content.attachments = [attachment] }
        lock.unlock()
        handler(content)
    }

    /// iOS only accepts a file whose name says what kind it is, and deletes the
    /// download as soon as its handler returns, so it's moved first.
    private static func attachment(from file: URL, response: URLResponse?, url: URL) -> UNNotificationAttachment? {
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) { return nil }
        let fileExtension = response?.mimeType.flatMap { UTType(mimeType: $0)?.preferredFilenameExtension }
            ?? (url.pathExtension.isEmpty ? ""jpg"" : url.pathExtension)
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension(fileExtension)
        do {
            try FileManager.default.moveItem(at: file, to: destination)
            return try UNNotificationAttachment(identifier: ""image"", url: destination)
        } catch {
            return nil
        }
    }
}
";
    }
}
#endif

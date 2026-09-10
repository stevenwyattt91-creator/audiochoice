import UIKit
import UserNotifications

/// Asks for permission to notify, hands Apple's device token to the server, and keeps the two in
/// step.
///
/// A scan takes minutes. Before this the only way to learn it had finished was to keep the import
/// screen open, because iOS suspends a backgrounded app and its polling loop stops with it. The
/// server already knows the moment a scan completes, so it tells the phone instead.
@MainActor
final class PushNotificationRegistrar: NSObject, ObservableObject {
    static let shared = PushNotificationRegistrar()

    /// The last token Apple issued, if this launch has been given one.
    private(set) var deviceToken: String?

    /// Remembered so sign-out can tell the server to forget this device even though Apple will not
    /// hand the token over again on demand.
    private static let storedTokenKey = "pushDeviceToken"

    private override init() { super.init() }

    /// Asks for permission if it has not been asked before, then registers.
    ///
    /// Deliberately not called at first launch. Being asked to allow notifications before doing
    /// anything is the prompt people refuse, and a refusal is close to permanent -- iOS will not ask
    /// again, and the listener has to find the setting themselves. Called once an import is under
    /// way instead, where the reason for it is obvious.
    func requestAuthorizationAndRegister() async {
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        switch settings.authorizationStatus {
        case .notDetermined:
            let granted = try? await center.requestAuthorization(options: [.alert, .sound, .badge])
            guard granted == true else { return }
        case .denied:
            // Nothing to do. Registering anyway would succeed and then deliver to a device that
            // shows nothing, which reads as the feature being broken rather than switched off.
            return
        default:
            break
        }
        UIApplication.shared.registerForRemoteNotifications()
    }

    /// Registers again if permission was already granted, without ever prompting.
    ///
    /// Run on launch so a reissued token reaches the server. Apple only delivers the token through
    /// the app delegate in response to this call, so there is no way to check it is still current
    /// other than asking again.
    func registerIfAlreadyAuthorized() async {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        guard settings.authorizationStatus == .authorized ||
              settings.authorizationStatus == .provisional else { return }
        UIApplication.shared.registerForRemoteNotifications()
    }

    /// Called by the app delegate with the token Apple issued.
    func accept(deviceToken data: Data) {
        let token = data.map { String(format: "%02x", $0) }.joined()
        deviceToken = token
        UserDefaults.standard.set(token, forKey: Self.storedTokenKey)
        Task {
            guard let client = try? CloudScanClient.configured() else { return }
            // Failure is not surfaced. The listener did not ask for this, and it is retried on the
            // next launch; telling them a background registration failed would be noise about
            // something they cannot act on.
            try? await client.registerPushDevice(token: token)
        }
    }

    /// This device's token, including one stored by an earlier launch.
    ///
    /// Read by sign-out, which has to capture it before the session is torn down. Apple only hands
    /// the token over in response to a registration call, so a copy is the only way to still know it
    /// at the moment it is needed.
    var storedDeviceToken: String? {
        deviceToken ?? UserDefaults.standard.string(forKey: Self.storedTokenKey)
    }

    /// Forgets the local copy. Separate from telling the server, because sign-out has to do the
    /// telling itself while it still holds a usable session.
    func forgetDeviceToken() {
        deviceToken = nil
        UserDefaults.standard.removeObject(forKey: Self.storedTokenKey)
    }
}

/// The app delegate exists only to receive the APNs token.
///
/// SwiftUI has no equivalent hook: `registerForRemoteNotifications()` answers through
/// `UIApplicationDelegate` and nowhere else, so a delegate is attached with
/// `UIApplicationDelegateAdaptor` purely to forward it.
final class PushAppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        Task { @MainActor in PushNotificationRegistrar.shared.accept(deviceToken: deviceToken) }
    }

    func application(
        _ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        // Expected in the simulator, and on a debug build whose APNs key is production-scoped.
        // Logged rather than shown: nothing the listener does affects it.
        NSLog("AudioChoice could not register for push notifications: \(error.localizedDescription)")
    }
}

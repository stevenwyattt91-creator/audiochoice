import SwiftUI

struct SettingsView: View {

    var body: some View {

        ZStack {

            Color.black.opacity(0.96)
                .ignoresSafeArea()

            VStack(spacing: 22) {

                Image(systemName: "slider.horizontal.3")
                    .font(.system(size: 70))
                    .foregroundStyle(.green)

                Text("Settings")
                    .font(.system(size: 34, weight: .bold))

                Text("""
Playback, scanning, fingerprinting,
and future account settings
will appear here.
""")
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

                Spacer()
            }
            .padding(40)
        }
    }
}

#Preview {

    SettingsView()
}

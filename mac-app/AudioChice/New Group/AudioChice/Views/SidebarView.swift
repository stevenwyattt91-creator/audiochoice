import SwiftUI

struct SidebarView: View {

    @Binding var selectedDestination: SidebarDestination?

    var body: some View {
        List(selection: $selectedDestination) {

            Section("AudioChoice") {

                ForEach(
                    SidebarDestination.allCases
                ) { destination in

                    Label(
                        destination.title,
                        systemImage: destination.icon
                    )
                    .tag(destination)
                }
            }
        }
        .navigationTitle("AudioChoice")
        .frame(minWidth: 220)
    }
}

#Preview {

    SidebarView(
        selectedDestination: .constant(.library)
    )
}

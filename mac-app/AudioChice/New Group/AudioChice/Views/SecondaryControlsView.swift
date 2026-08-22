import SwiftUI

struct SecondaryControlsView: View {

    let sleep:()->Void
    let chapters:()->Void
    let notes:()->Void
    let filters:()->Void

    var body: some View {

        HStack(spacing:48){

            control(
                "Sleep",
                "moon.zzz",
                sleep
            )

            control(
                "Chapters",
                "list.bullet.rectangle",
                chapters
            )

            control(
                "Bookmarks",
                "bookmark.text",
                notes
            )

            control(
                "Filters",
                "line.3.horizontal.decrease.circle",
                filters
            )
        }
    }

    private func control(
        _ title:String,
        _ icon:String,
        _ action:@escaping()->Void
    )->some View{

        VStack{

            Button(
                action: action
            ){

                Image(systemName: icon)
                    .font(.title2)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)

            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

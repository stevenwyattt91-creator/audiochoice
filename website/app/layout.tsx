import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  metadataBase: new URL("https://audiochoiceapp.com"),
  title: "AudioChoice — Listen Your Way",
  description: "A private audiobook player that lets you find and filter sensitive content without giving up the story.",
  icons: { icon: "/favicon.png", shortcut: "/favicon.png" },
  openGraph: {
    title: "AudioChoice — Listen Your Way",
    description: "Your audiobooks. Your boundaries. Coming soon to Android and Apple.",
    type: "website",
    images: [{ url: "/og.png", width: 1200, height: 630, alt: "AudioChoice — Listen Your Way" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "AudioChoice — Listen Your Way",
    description: "Your audiobooks. Your boundaries. Coming soon to Android and Apple.",
    images: ["/og.png"],
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="en"><body>{children}</body></html>;
}

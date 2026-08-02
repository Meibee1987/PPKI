import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = { title: "PPKI IPB Smart Formatter", description: "Audit dan auto-format DOCX berdasarkan PPKI IPB" };
export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="id"><body>{children}</body></html>;
}

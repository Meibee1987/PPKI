export function isProtectedPath(pathname: string): boolean {
  return pathname === "/"
    || pathname === "/documents"
    || pathname.startsWith("/documents/")
    || pathname === "/audits"
    || pathname.startsWith("/audits/");
}

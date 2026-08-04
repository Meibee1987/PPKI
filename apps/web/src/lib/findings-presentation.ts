import type { AuditFinding, JsonValue, ScoreState } from "./audit-contract";

export type DisplayRow = { label: string; value: string };
export type LocationPresentation = { primary: string; compact: string | null; details: string[]; accessibleLabel: string };
export type FindingGuidance = {
  title: string;
  issue: string;
  expected: string;
  repairStatus: string;
  afterTitle: string;
  afterDetail: string;
  steps: string[];
};

const sensitiveKeys = ["text", "title", "filename", "path", "url", "xml", "stack", "exception", "content"];
const labels: Record<string, string> = {
  property: "Properti", normalizedvalue: "Nilai ternormalisasi", rawvalue: "Nilai numerik",
  expectedvalue: "Nilai yang diharapkan", value: "Nilai", unit: "Satuan", tolerance: "Toleransi",
  acceptedvalues: "Nilai yang diterima", resolutionstate: "Status resolusi", sourcekind: "Jenis sumber",
  sourcestyleid: "ID gaya sumber", inherited: "Diwariskan", direct: "Langsung", semantickind: "Jenis semantik",
  count: "Jumlah", presence: "Keberadaan", zone: "Zona", classificationstate: "Status klasifikasi",
  diagnosticcode: "Kode diagnostik", contractsource: "Sumber kontrak", validationkey: "Kunci validasi",
  minimum: "Minimum", maximum: "Maksimum", required: "Wajib", order: "Urutan",
};

export function findingGuidance(finding: Pick<AuditFinding, "ruleCode" | "validationKey" | "element" | "reasonCode" | "actual" | "expected" | "actionAvailability">): FindingGuidance {
  const property = payloadString(finding.actual, "property") ?? payloadString(finding.expected, "property");
  const normalized = payloadString(finding.actual, "normalizedValue");
  const accepted = payloadStrings(finding.expected, "acceptedValues");
  const missingEnglishSummary = property?.toLowerCase().includes("summaryenglish")
    || finding.validationKey === "summary.thesis-dissertation-language-pair";
  const requiredPresence = normalized?.toLowerCase() === "absent"
    && accepted.some(value => value.toLowerCase() === "present");

  if (missingEnglishSummary && (requiredPresence || finding.reasonCode === "semantic-section-required")) {
    return {
      title: "Abstrak bahasa Inggris belum terdeteksi",
      issue: "Pemeriksaan snapshot tidak menemukan bagian abstrak berbahasa Inggris pada struktur dokumen.",
      expected: "Dokumen harus memiliki bagian abstrak bahasa Inggris yang dapat dikenali sebagai bagian tersendiri.",
      repairStatus: "Perbaikan manual diperlukan",
      afterTitle: "Belum ada hasil perbaikan",
      afterDetail: "Sesudah aktual baru dapat dibuktikan dari versi dokumen baru dan audit ulang.",
      steps: [
        "Periksa apakah abstrak bahasa Inggris sudah tersedia.",
        "Pastikan judul dan strukturnya mengikuti pedoman PPKI serta tidak berada di text box atau tabel.",
        "Unggah hasil sebagai versi baru, lalu jalankan audit ulang untuk membuktikan perbaikannya.",
      ],
    };
  }

  if (property?.toLowerCase() === "alignment" && accepted.some(value => value.toLowerCase() === "both")) {
    return {
      title: `${finding.element} belum rata kiri-kanan`,
      issue: "Perataan yang terbaca belum menggunakan justified/rata kiri-kanan.",
      expected: "Paragraf harus menggunakan perataan rata kiri-kanan sesuai aturan PPKI.",
      repairStatus: finding.actionAvailability === "None" ? "Belum ada aksi perbaikan" : "Perbaikan tersedia",
      afterTitle: "Belum ada hasil perbaikan",
      afterDetail: "Sesudah aktual hanya ditampilkan setelah hasil perbaikan disimpan sebagai versi baru dan diaudit ulang.",
      steps: [
        "Buka paragraf pada lokasi yang ditunjukkan.",
        "Ubah perataan paragraf menjadi rata kiri-kanan.",
        "Simpan sebagai versi baru dan jalankan audit ulang.",
      ],
    };
  }

  return {
    title: `${finding.element} belum sesuai`,
    issue: `Nilai yang terbaca belum memenuhi aturan ${finding.ruleCode}.`,
    expected: "Sesuaikan bagian ini dengan nilai yang diwajibkan pada panel target perbaikan.",
    repairStatus: finding.actionAvailability === "None" ? "Perbaikan manual diperlukan" : "Perbaikan tersedia",
    afterTitle: "Belum ada hasil perbaikan",
    afterDetail: "Upload versi baru dan audit ulang diperlukan untuk menampilkan sesudah aktual.",
    steps: [
      "Periksa bagian dokumen pada lokasi yang ditunjukkan.",
      "Bandingkan detail teknis sebelum dengan target perbaikan.",
      "Simpan perubahan sebagai versi baru dan jalankan audit ulang.",
    ],
  };
}

function payloadString(value: JsonValue, expectedKey: string): string | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return null;
  const entry = Object.entries(value).find(([key]) => normalizeKey(key) === normalizeKey(expectedKey));
  return entry && typeof entry[1] === "string" ? entry[1] : null;
}

function payloadStrings(value: JsonValue, expectedKey: string): string[] {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return [];
  const entry = Object.entries(value).find(([key]) => normalizeKey(key) === normalizeKey(expectedKey));
  return entry && Array.isArray(entry[1]) ? entry[1].filter(item => typeof item === "string") : [];
}

export function presentPayload(value: JsonValue, limit = 12): DisplayRow[] {
  const rows: DisplayRow[] = [];
  visit(value, rows, "Nilai", 0, Math.max(1, Math.min(limit, 20)));
  return rows;
}

function visit(value: JsonValue, rows: DisplayRow[], label: string, depth: number, limit: number): void {
  if (rows.length >= limit) return;
  if (value === null || typeof value !== "object") {
    rows.push({ label, value: scalar(value) });
    return;
  }
  if (depth >= 2) {
    rows.push({ label, value: "Data terstruktur" });
    return;
  }
  if (Array.isArray(value)) {
    const safeItems = value.slice(0, 6).filter(item => item === null || typeof item !== "object");
    rows.push({ label, value: safeItems.length ? safeItems.map(scalar).join(", ") : "Data terstruktur" });
    return;
  }
  for (const [key, child] of Object.entries(value)) {
    if (rows.length >= limit || isSensitiveKey(key)) continue;
    const childLabel = labels[normalizeKey(key)] ?? readableKey(key);
    visit(child, rows, childLabel, depth + 1, limit);
  }
}

function scalar(value: null | boolean | number | string): string {
  if (value === null) return "Tidak tersedia";
  if (typeof value === "boolean") return value ? "Ya" : "Tidak";
  if (typeof value === "number") return String(value);
  const normalized = value.replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return normalized.length > 120 ? `${normalized.slice(0, 117)}…` : normalized || "Kosong";
}

function normalizeKey(key: string): string { return key.replace(/[^a-z0-9]/gi, "").toLowerCase(); }
function isSensitiveKey(key: string): boolean {
  const normalized = normalizeKey(key);
  return sensitiveKeys.some(sensitive => normalized === sensitive || normalized.endsWith(sensitive));
}
function readableKey(key: string): string {
  const normalized = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim();
  return normalized ? normalized.charAt(0).toUpperCase() + normalized.slice(1) : "Nilai";
}

export function presentLocation(value: JsonValue): LocationPresentation {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return fallbackLocation();
  const data = value as Record<string, JsonValue>;
  const compact = compactLocation(data);
  const section = nonNegativeIndex(data, "sectionIndex");
  const body = nonNegativeIndex(data, "bodyElementIndex");
  const paragraph = nonNegativeIndex(data, "paragraphIndex");
  const run = nonNegativeIndex(data, "runIndex");
  const details: string[] = [];
  if (section !== null) details.push(`Bagian ${section + 1}`);
  if (body !== null) details.push(`Elemen dokumen ${body + 1}`);
  if (paragraph !== null) details.push(`Paragraf ${paragraph + 1}`);
  if (run !== null) details.push(`Segmen format ${run + 1}`);
  if (!details.length) return { primary: "Dokumen", compact, details: [], accessibleLabel: compact ? `Lokasi: seluruh dokumen; referensi ${compact}` : "Lokasi: seluruh dokumen" };
  return { primary: details.at(-1)!, compact, details, accessibleLabel: `Lokasi: ${details.join(", ")}${compact ? `; referensi ${compact}` : ""}` };
}

function compactLocation(data: Record<string, JsonValue>): string | null {
  const entry = Object.entries(data).find(([key]) => normalizeKey(key) === "compactlocation");
  if (!entry || typeof entry[1] !== "string") return null;
  const safe = entry[1].replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return safe ? (safe.length > 96 ? `${safe.slice(0, 93)}…` : safe) : null;
}

function nonNegativeIndex(data: Record<string, JsonValue>, expected: string): number | null {
  const entry = Object.entries(data).find(([key]) => normalizeKey(key) === normalizeKey(expected));
  return entry && typeof entry[1] === "number" && Number.isInteger(entry[1]) && entry[1] >= 0 ? entry[1] : null;
}

function fallbackLocation(): LocationPresentation {
  return { primary: "Lokasi tidak tersedia", compact: null, details: [], accessibleLabel: "Lokasi tidak tersedia" };
}

export function scorePresentation(state: ScoreState, score: number | null, policyVersion: string | null): { title: string; detail: string } {
  switch (state) {
    case "Calculated":
      return score !== null
        ? { title: String(score), detail: policyVersion ? `Kebijakan ${policyVersion}` : "Skor terhitung" }
        : { title: "Skor tidak tersedia", detail: "Respons skor tidak lengkap" };
    case "NotConfigured": return { title: "Skor belum dikonfigurasi", detail: "Belum ada kebijakan penilaian formal" };
    case "AuditIncomplete": return { title: "Skor belum tersedia", detail: "Audit belum selesai" };
    case "InvalidConfiguration": return { title: "Skor tidak tersedia", detail: "Konfigurasi penilaian tidak valid" };
    case "NotApplicable": return { title: "Skor tidak berlaku", detail: "Tidak ada aturan yang berlaku" };
  }
}

export function pageRange(page: number, pageSize: number, totalCount: number): { start: number; end: number; totalPages: number } {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  if (totalCount === 0) return { start: 0, end: 0, totalPages };
  return { start: (page - 1) * pageSize + 1, end: Math.min(page * pageSize, totalCount), totalPages };
}

export function formatTimestamp(value: string | null): string {
  if (!value) return "Belum tersedia";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "Tidak valid" : new Intl.DateTimeFormat("id-ID", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

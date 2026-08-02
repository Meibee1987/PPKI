# Template prompt task Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task <ID>: <JUDUL>.

Tujuan:
<TUJUAN TUNGGAL>

Baca konteks minimum:
- AGENTS.md
- <FILE RELEVAN 1>
- <FILE RELEVAN 2>

Bagian implementasi:
A. <BAGIAN A>
B. <BAGIAN B>
C. <BAGIAN C>

Batasan:
- Jangan lanjut ke task lain.
- Jangan melakukan refactor luas.
- Jangan commit atau log secret/token/connection string/isi dokumen.
- Original DOCX immutable; mutasi menghasilkan DocumentVersion baru.
- Migration additive.
- Tambahkan test relevan.

Acceptance criteria:
- <KRITERIA 1>
- <KRITERIA 2>

Verifikasi:
- <COMMAND 1>
- <COMMAND 2>

Jawaban akhir harus memuat: ringkasan, file diubah, migration/API contract, command test dan hasil, risiko/verifikasi manual tersisa.
```

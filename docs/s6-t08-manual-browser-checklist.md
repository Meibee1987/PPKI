# S6-T08 Manual Browser Checklist

Checklist ini belum dianggap selesai sampai setiap langkah diamati pada local stack dengan akun dan audit uji yang sah.

## Viewport dan reflow

- [ ] Desktop: ringkasan, kesiapan, filter, daftar, dan drawer terbaca tanpa horizontal page scrolling.
- [ ] Tablet/narrow: metrik dan filter reflow; pagination serta semua tindakan tetap terlihat.
- [ ] Mobile: drawer memenuhi viewport, dapat digulir, dan Actual/Expected, sumber, alasan, serta dialog tidak overflow.
- [ ] Nama aturan, kode, bagian sumber, dan alasan panjang membungkus tanpa menutupi kontrol.

## Keyboard dan fokus

- [ ] Gunakan Tab/Shift+Tab untuk search, seluruh filter, pagination, dan tombol detail.
- [ ] Buka drawer dengan keyboard; fokus masuk ke tombol Tutup.
- [ ] Tab/Shift+Tab tetap di dalam drawer dan Escape menutup drawer.
- [ ] Setelah drawer ditutup, fokus kembali ke tombol detail yang membukanya.
- [ ] Buka glossary sumber dengan keyboard dan pastikan tidak ada tautan sumber palsu.
- [ ] Muat cuplikan struktural hanya melalui tombol eksplisit.
- [ ] Isi alasan, buka konfirmasi ManualReview/Ignore, lalu pastikan fokus masuk ke Batal.
- [ ] Escape pertama menutup dialog konfirmasi saja; drawer tetap terbuka dan fokus kembali ke pemicu.
- [ ] Batal dan konfirmasi dapat dioperasikan dengan Enter/Space.

## Status dan assistive technology

- [ ] Heading audit, kesiapan, ringkasan, daftar, drawer, sumber, dan review membentuk urutan yang masuk akal.
- [ ] Label search, semua filter, alasan, pagination, tombol detail, dan tombol tutup diumumkan jelas.
- [ ] Status audit queued/processing/completed/failed dan readiness dapat dipahami tanpa mengandalkan warna.
- [ ] Loading, refresh, error, dan sukses penting diumumkan tanpa AbortError atau detail internal.
- [ ] Polling normal tidak menghasilkan pengumuman berulang yang mengganggu.

## State dan keamanan

- [ ] Filter/search/pagination bertahan setelah drawer dibuka dan ditutup.
- [ ] ManualReview dan Ignore tetap membutuhkan alasan dan konfirmasi.
- [ ] Ignore tidak ditampilkan sebagai VerifiedResolved dan blocker tetap backend-authoritative.
- [ ] Source metadata menampilkan PDF dan printed page secara terpisah tanpa inferensi atau URL palsu.
- [ ] Tidak ada signed URL, bucket path, local path, token, atau isi dokumen lengkap yang terlihat.
- [ ] Pergantian temuan cepat tidak menampilkan detail/source dari respons lama.
- [ ] Jalur 401 mengarah ke login dengan aman jika praktis diuji.

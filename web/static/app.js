// Kamerayla barkod/QR okuma (Android Chrome; HTTPS veya localhost gerekir).
// Desteklenmezse 📷 butonları gizli kalır; telefonun kendi kamera uygulamasıyla QR etiket okutulabilir.
let _stream = null, _timer = null;
const canScan = window.isSecureContext && 'BarcodeDetector' in window && navigator.mediaDevices;
document.addEventListener('DOMContentLoaded', () => {
  if (!canScan) return;
  document.querySelectorAll('.scan-btn').forEach(b => {
    b.hidden = false;
    b.addEventListener('click', () => {
      const t = b.dataset.scanTarget === 'prev' ? b.previousElementSibling : document.getElementById(b.dataset.scanTarget);
      startScan(t, b.dataset.scanTarget === 'prev');
    });
  });
});
async function startScan(input, submit) {
  const dlg = document.getElementById('scanDlg'), v = document.getElementById('scanVideo');
  try { _stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } }); }
  catch (e) { alert('Kamera açılamadı: ' + e.message); return; }
  v.srcObject = _stream; await v.play(); dlg.showModal();
  const det = new BarcodeDetector();
  _timer = setInterval(async () => {
    const r = await det.detect(v).catch(() => []);
    if (!r.length) return;
    let val = r[0].rawValue;
    stopScan();
    if (/^https?:\/\//.test(val)) { location.href = val; return; } // sistemin kendi QR etiketi
    input.value = val;
    input.dispatchEvent(new Event('scanned'));
    if (submit) input.form.submit();
  }, 300);
}
function stopScan() {
  clearInterval(_timer);
  if (_stream) _stream.getTracks().forEach(t => t.stop());
  const d = document.getElementById('scanDlg'); if (d.open) d.close();
}

"""
Fabrika Stok & Ambar Yönetim Sistemi  (v2)
------------------------------------------
Sunucuda çalışan, tarayıcıdan (PC + telefon) erişilen, rol/yetki tabanlı ambar uygulaması.

Özellikler:
  * Ürün kartı: marka, model/parça no, kategori ağacı, teknik özellik, fotoğraf, dokümanlar
  * Hiyerarşik ambar lokasyonları (Depo > Bölge/Koridor > Raf > Göz), lokasyon bazlı stok
  * Giriş (tedarikçi, fatura/irsaliye no, birim fiyat, fatura dosyası), Çıkış, Transfer, Sayım
  * Malzeme Talebi -> Onay -> Ambardan Karşılama iş akışı (üretim, tasarım, bakım...)
  * Tedarikçiler, fiyat geçmişi, sipariş önerisi, tüketim / stok değeri / hareketsiz stok raporları
  * QR etiket (telefon kamerasıyla okutunca ürün/lokasyon sayfası açılır)
  * Rol şablonları (Müdür, Ambar, Satın Alma, Üretim, Tasarım, Bakım...) + kişiye özel yetkiler
  * Denetim kaydı (kim neyi ne zaman değiştirdi)

Çalıştırma:
    pip install -r requirements.txt
    python app.py              -> http://SUNUCU_IP:8000
İlk giriş: admin / admin123 (ilk girişte değiştirilmesi zorunludur).
"""
import csv
import io
import mimetypes
import os
import secrets
import sqlite3
from datetime import datetime
from functools import wraps

from flask import (Flask, Response, abort, flash, g, redirect, render_template,
                   request, send_file, session, url_for)
from markupsafe import Markup
from werkzeug.security import check_password_hash, generate_password_hash

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.environ.get("STOK_DB", os.path.join(BASE_DIR, "stok.db"))
UPLOAD_DIR = os.environ.get("STOK_UPLOADS", os.path.join(BASE_DIR, "uploads"))
SECRET_FILE = os.path.join(BASE_DIR, ".secret_key")
COMPANY = os.environ.get("STOK_FIRMA", "Fabrika Ambar")

ALLOWED_EXT = {"pdf", "jpg", "jpeg", "png", "webp", "gif", "xlsx", "xls", "docx", "doc",
               "csv", "txt", "dwg", "dxf", "step", "stp", "igs", "iges", "zip", "7z", "rar",
               "eplan", "zw1", "edz"}
INLINE_EXT = {"pdf", "jpg", "jpeg", "png", "webp", "gif"}
IMAGE_EXT = {"jpg", "jpeg", "png", "webp", "gif"}
ATTACH_KINDS = ["Fatura", "İrsaliye", "Teklif", "Datasheet", "Teknik Resim", "Fotoğraf",
                "Sertifika", "Kullanım Kılavuzu", "Diğer"]
LOC_TYPES = ["Depo", "Bölge", "Koridor", "Raf", "Göz", "Saha", "Makine Yanı", "Diğer"]
CURRENCIES = ["TRY", "EUR", "USD"]
CUR_SYM = {"TRY": "₺", "EUR": "€", "USD": "$"}

# Yetki tanımları: (kolon adı, ekranda görünen açıklama)
PERMISSIONS = [
    ("can_in", "Stok Girişi (mal kabul)"),
    ("can_out", "Stok Çıkışı / Talep Karşılama"),
    ("can_transfer", "Lokasyonlar Arası Transfer"),
    ("can_count", "Sayım Düzeltme"),
    ("can_edit", "Ürün / Kategori / Lokasyon Tanımlama"),
    ("can_request", "Malzeme Talebi Oluşturma"),
    ("can_approve", "Malzeme Talebi Onaylama"),
    ("can_purchase", "Tedarikçi Yönetimi"),
    ("can_see_price", "Fiyat, Fatura ve Stok Değeri Görme"),
    ("can_report", "Raporlar ve Excel'e Aktarma"),
    ("is_admin", "Sistem Yöneticisi (her şey + kullanıcı yönetimi)"),
]
PERM_KEYS = [p[0] for p in PERMISSIONS]

# Rol şablonları: kullanıcı formunda seçilince yetki kutuları otomatik işaretlenir
ROLES = {
    "Fabrika Müdürü": ["can_request", "can_approve", "can_see_price", "can_report"],
    "Ambar Sorumlusu": ["can_in", "can_out", "can_transfer", "can_count", "can_edit",
                        "can_request", "can_report"],
    "Ambar Personeli": ["can_in", "can_out", "can_transfer"],
    "Satın Alma": ["can_in", "can_edit", "can_request", "can_purchase", "can_see_price",
                   "can_report"],
    "Üretim Şefi": ["can_request", "can_approve", "can_report"],
    "Üretim Personeli": ["can_request"],
    "Tasarım / Mühendislik": ["can_edit", "can_request", "can_report"],
    "Bakım / Elektrik": ["can_request"],
    "Kalite Kontrol": ["can_report"],
    "Muhasebe": ["can_see_price", "can_report"],
    "Sadece İzleme": [],
    "Sistem Yöneticisi": PERM_KEYS,
}

REQ_STATUS = {
    "BEKLIYOR": "Onay Bekliyor",
    "ONAYLANDI": "Onaylandı – Ambarda",
    "KISMI": "Kısmen Teslim",
    "TAMAMLANDI": "Teslim Edildi",
    "REDDEDILDI": "Reddedildi",
    "IPTAL": "İptal",
}
MOVE_LABEL = {"GIRIS": "Giriş", "CIKIS": "Çıkış", "TRANSFER": "Transfer", "SAYIM": "Sayım"}


def load_secret_key():
    if os.environ.get("STOK_SECRET"):
        return os.environ["STOK_SECRET"]
    if os.path.exists(SECRET_FILE):
        with open(SECRET_FILE) as f:
            return f.read().strip()
    key = secrets.token_hex(32)
    with open(SECRET_FILE, "w") as f:
        f.write(key)
    return key


app = Flask(__name__)
app.config.update(
    SECRET_KEY=load_secret_key(),
    SESSION_COOKIE_HTTPONLY=True,
    SESSION_COOKIE_SAMESITE="Lax",
    PERMANENT_SESSION_LIFETIME=60 * 60 * 12,  # 12 saat
    MAX_CONTENT_LENGTH=40 * 1024 * 1024,      # tek istekte en fazla 40 MB dosya
)


# =========================================================================== DB
def get_db():
    if "db" not in g:
        g.db = sqlite3.connect(DB_PATH, timeout=15)
        g.db.row_factory = sqlite3.Row
        g.db.execute("PRAGMA foreign_keys = ON")
    return g.db


@app.teardown_appcontext
def close_db(_exc):
    db = g.pop("db", None)
    if db is not None:
        db.close()


SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL COLLATE NOCASE,
    full_name TEXT NOT NULL,
    department TEXT NOT NULL DEFAULT '',
    role TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    password_hash TEXT NOT NULL,
    active INTEGER NOT NULL DEFAULT 1,
    must_change_pw INTEGER NOT NULL DEFAULT 0,
    can_in INTEGER NOT NULL DEFAULT 0,
    can_out INTEGER NOT NULL DEFAULT 0,
    can_transfer INTEGER NOT NULL DEFAULT 0,
    can_count INTEGER NOT NULL DEFAULT 0,
    can_edit INTEGER NOT NULL DEFAULT 0,
    can_request INTEGER NOT NULL DEFAULT 0,
    can_approve INTEGER NOT NULL DEFAULT 0,
    can_purchase INTEGER NOT NULL DEFAULT 0,
    can_see_price INTEGER NOT NULL DEFAULT 0,
    can_report INTEGER NOT NULL DEFAULT 0,
    is_admin INTEGER NOT NULL DEFAULT 0,
    last_login TEXT,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER REFERENCES categories(id),
    name TEXT NOT NULL,
    UNIQUE(parent_id, name)
);
CREATE TABLE IF NOT EXISTS locations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER REFERENCES locations(id),
    code TEXT UNIQUE NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL,
    type TEXT NOT NULL DEFAULT 'Raf',
    description TEXT NOT NULL DEFAULT '',
    active INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS suppliers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL COLLATE NOCASE,
    contact TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    email TEXT NOT NULL DEFAULT '',
    tax_office TEXT NOT NULL DEFAULT '',
    tax_no TEXT NOT NULL DEFAULT '',
    address TEXT NOT NULL DEFAULT '',
    website TEXT NOT NULL DEFAULT '',
    note TEXT NOT NULL DEFAULT '',
    active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS products (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT UNIQUE NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    category_id INTEGER REFERENCES categories(id),
    brand TEXT NOT NULL DEFAULT '',
    model TEXT NOT NULL DEFAULT '',
    barcode TEXT NOT NULL DEFAULT '',
    tech_spec TEXT NOT NULL DEFAULT '',
    unit TEXT NOT NULL DEFAULT 'adet',
    min_stock REAL NOT NULL DEFAULT 0,
    max_stock REAL NOT NULL DEFAULT 0,
    default_location_id INTEGER REFERENCES locations(id),
    default_supplier_id INTEGER REFERENCES suppliers(id),
    last_price REAL NOT NULL DEFAULT 0,
    currency TEXT NOT NULL DEFAULT 'TRY',
    quantity REAL NOT NULL DEFAULT 0,
    image_id INTEGER,
    active INTEGER NOT NULL DEFAULT 1,
    created_by INTEGER REFERENCES users(id),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS stock (
    product_id INTEGER NOT NULL REFERENCES products(id),
    location_id INTEGER NOT NULL REFERENCES locations(id),
    qty REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (product_id, location_id)
);
CREATE TABLE IF NOT EXISTS requests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    requester_id INTEGER NOT NULL REFERENCES users(id),
    department TEXT NOT NULL DEFAULT '',
    project TEXT NOT NULL DEFAULT '',
    needed_date TEXT NOT NULL DEFAULT '',
    priority TEXT NOT NULL DEFAULT 'Normal',
    note TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'BEKLIYOR',
    approver_id INTEGER REFERENCES users(id),
    approved_at TEXT,
    decision_note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS request_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id INTEGER NOT NULL REFERENCES requests(id),
    product_id INTEGER NOT NULL REFERENCES products(id),
    qty REAL NOT NULL,
    issued_qty REAL NOT NULL DEFAULT 0,
    note TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS movements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id INTEGER NOT NULL REFERENCES products(id),
    user_id INTEGER NOT NULL REFERENCES users(id),
    type TEXT NOT NULL CHECK (type IN ('GIRIS','CIKIS','TRANSFER','SAYIM')),
    qty REAL NOT NULL,
    from_location_id INTEGER REFERENCES locations(id),
    to_location_id INTEGER REFERENCES locations(id),
    qty_before REAL NOT NULL,
    qty_after REAL NOT NULL,
    supplier_id INTEGER REFERENCES suppliers(id),
    unit_price REAL,
    currency TEXT,
    document_no TEXT NOT NULL DEFAULT '',
    document_date TEXT NOT NULL DEFAULT '',
    project TEXT NOT NULL DEFAULT '',
    receiver TEXT NOT NULL DEFAULT '',
    request_id INTEGER REFERENCES requests(id),
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS attachments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    kind TEXT NOT NULL DEFAULT 'Diğer',
    filename TEXT NOT NULL,
    stored_name TEXT NOT NULL,
    size INTEGER NOT NULL,
    user_id INTEGER REFERENCES users(id),
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER,
    action TEXT NOT NULL,
    detail TEXT NOT NULL DEFAULT '',
    ip TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mov_product ON movements(product_id);
CREATE INDEX IF NOT EXISTS idx_mov_date ON movements(created_at);
CREATE INDEX IF NOT EXISTS idx_mov_request ON movements(request_id);
CREATE INDEX IF NOT EXISTS idx_stock_loc ON stock(location_id);
CREATE INDEX IF NOT EXISTS idx_att ON attachments(entity, entity_id);
CREATE INDEX IF NOT EXISTS idx_req_status ON requests(status);
"""


def now():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def init_db():
    os.makedirs(UPLOAD_DIR, exist_ok=True)
    db = sqlite3.connect(DB_PATH)
    db.execute("PRAGMA journal_mode=WAL")  # Eşzamanlı okuma/yazma için
    cols = [r[1] for r in db.execute("PRAGMA table_info(products)")]
    if cols and "brand" not in cols:
        raise SystemExit(
            "!! stok.db eski sürüme (v1) ait. Yedekleyip silin ya da farklı bir STOK_DB yolu "
            "verin; ürünleri 'CSV İçe Aktar' ile yeniden yükleyebilirsiniz.")
    db.executescript(SCHEMA)
    if db.execute("SELECT COUNT(*) FROM users").fetchone()[0] == 0:
        db.execute(
            "INSERT INTO users (username, full_name, role, password_hash, must_change_pw, "
            + ", ".join(PERM_KEYS) + ", created_at) VALUES (?,?,?,?,1,"
            + ",".join("1" * len(PERM_KEYS)) + ",?)",
            ("admin", "Sistem Yöneticisi", "Sistem Yöneticisi",
             generate_password_hash("admin123"), now()))
        print(">> İlk kullanıcı oluşturuldu: admin / admin123 (ilk girişte değiştirin)")
    if db.execute("SELECT COUNT(*) FROM locations").fetchone()[0] == 0:
        db.execute("INSERT INTO locations (code, name, type) VALUES ('ANA','Ana Ambar','Depo')")
    db.commit()
    db.close()


def audit(action, detail=""):
    get_db().execute(
        "INSERT INTO audit (user_id, action, detail, ip, created_at) VALUES (?,?,?,?,?)",
        (g.user["id"] if g.get("user") else None, action, detail[:1000],
         request.remote_addr or "", now()))


# ------------------------------------------------------------ Ağaç yardımcıları
def tree_paths(table, label="name"):
    """{id: 'Ana Ambar / A Bölgesi / Raf 3'} sözlüğü."""
    rows = {r["id"]: r for r in get_db().execute(f"SELECT * FROM {table}")}
    cache = {}

    def path(i, depth=0):
        if i in cache:
            return cache[i]
        r = rows.get(i)
        if r is None or depth > 20:
            return ""
        p = r[label] if not r["parent_id"] else f"{path(r['parent_id'], depth + 1)} / {r[label]}"
        cache[i] = p
        return p
    return {i: path(i) for i in rows}


def subtree_ids(table, root_id):
    return [r[0] for r in get_db().execute(
        f"WITH RECURSIVE t(id) AS (SELECT ? UNION ALL SELECT c.id FROM {table} c "
        f"JOIN t ON c.parent_id=t.id) SELECT id FROM t", (root_id,))]


def location_options(only_active=True):
    paths = tree_paths("locations")
    rows = get_db().execute("SELECT id, code, active FROM locations").fetchall()
    opts = [(r["id"], f"{paths[r['id']]}  [{r['code']}]") for r in rows
            if r["active"] or not only_active]
    return sorted(opts, key=lambda x: x[1].lower())


def category_options():
    paths = tree_paths("categories")
    return sorted(paths.items(), key=lambda x: x[1].lower())


# ================================================================ Güvenlik
@app.before_request
def load_user_and_csrf():
    g.user = None
    uid = session.get("uid")
    if uid:
        user = get_db().execute("SELECT * FROM users WHERE id=? AND active=1", (uid,)).fetchone()
        if user is None:
            session.clear()
        else:
            g.user = user
    if "csrf" not in session:
        session["csrf"] = secrets.token_hex(16)
    if request.method == "POST":
        if request.form.get("csrf") != session.get("csrf"):
            abort(400, "Geçersiz form anahtarı (CSRF). Sayfayı yenileyip tekrar deneyin.")
    if g.user and g.user["must_change_pw"] and request.endpoint not in (
            "change_password", "logout", "static"):
        return redirect(url_for("change_password"))


@app.after_request
def security_headers(resp):
    resp.headers.setdefault("X-Content-Type-Options", "nosniff")
    resp.headers.setdefault("X-Frame-Options", "SAMEORIGIN")
    resp.headers.setdefault("Referrer-Policy", "same-origin")
    return resp


def can(perm):
    u = g.get("user")
    if not u:
        return False
    if u["is_admin"]:
        return True
    if perm == "view":
        return True
    return bool(u[perm])


def can_any(*perms):
    return any(can(p) for p in perms)


def login_required(f):
    @wraps(f)
    def wrapper(*a, **kw):
        if not g.user:
            return redirect(url_for("login", next=request.full_path.rstrip("?")))
        return f(*a, **kw)
    return wrapper


def permission_required(*perms):
    def deco(f):
        @wraps(f)
        @login_required
        def wrapper(*a, **kw):
            if not can_any(*perms):
                abort(403)
            return f(*a, **kw)
        return wrapper
    return deco


# ============================================================ Biçimlendirme
def parse_num(value, default=None):
    if value is None or str(value).strip() == "":
        return default
    s = str(value).strip().replace(" ", "")
    if "," in s:  # Türkçe biçim: 1.234,5
        s = s.replace(".", "").replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None


def fmt_qty(q):
    if q is None:
        return ""
    return f"{q:,.3f}".rstrip("0").rstrip(".").replace(",", "X").replace(".", ",").replace("X", ".")


def fmt_money(v, cur="TRY"):
    if v is None:
        return ""
    s = f"{v:,.2f}".replace(",", "X").replace(".", ",").replace("X", ".")
    return f"{s} {CUR_SYM.get(cur or 'TRY', cur or '')}"


def xl(q):
    """CSV/Excel için binlik ayırıcısız sayı."""
    return "" if q is None else fmt_qty(q).replace(".", "")


def req_no(rid, created_at=""):
    return f"MT-{(created_at or now())[:4]}-{rid:05d}"


app.jinja_env.filters["qty"] = fmt_qty
app.jinja_env.filters["money"] = fmt_money
app.jinja_env.filters["nl2br"] = lambda s: Markup("<br>".join(
    Markup.escape(x) for x in (s or "").splitlines()))


@app.context_processor
def inject_globals():
    ctx = {"csrf_token": session.get("csrf", ""), "can": can, "can_any": can_any,
           "PERMISSIONS": PERMISSIONS, "COMPANY": COMPANY, "MOVE_LABEL": MOVE_LABEL,
           "REQ_STATUS": REQ_STATUS, "req_no": req_no, "badges": {}}
    if g.get("user"):
        db = get_db()
        b = {}
        if can("can_approve"):
            b["approve"] = db.execute(
                "SELECT COUNT(*) FROM requests WHERE status='BEKLIYOR'").fetchone()[0]
        if can("can_out"):
            b["issue"] = db.execute(
                "SELECT COUNT(*) FROM requests WHERE status IN ('ONAYLANDI','KISMI')").fetchone()[0]
        ctx["badges"] = b
    return ctx


# ================================================================ Dosyalar
def save_attachments(entity, entity_id, files, kind):
    saved = 0
    for f in files:
        if not f or not f.filename:
            continue
        ext = f.filename.rsplit(".", 1)[-1].lower() if "." in f.filename else ""
        if ext not in ALLOWED_EXT:
            flash(f"'{f.filename}' türü desteklenmiyor ({ext}).", "error")
            continue
        sub = datetime.now().strftime("%Y/%m")
        os.makedirs(os.path.join(UPLOAD_DIR, sub), exist_ok=True)
        stored = f"{sub}/{secrets.token_hex(12)}.{ext}"
        path = os.path.join(UPLOAD_DIR, stored)
        f.save(path)
        name = os.path.basename(f.filename.replace("\\", "/"))[:200]
        cur = get_db().execute(
            "INSERT INTO attachments (entity, entity_id, kind, filename, stored_name, size, "
            "user_id, created_at) VALUES (?,?,?,?,?,?,?,?)",
            (entity, entity_id, kind if kind in ATTACH_KINDS else "Diğer", name, stored,
             os.path.getsize(path), g.user["id"], now()))
        if entity == "product" and ext in IMAGE_EXT:
            get_db().execute("UPDATE products SET image_id=COALESCE(image_id, ?) WHERE id=?",
                             (cur.lastrowid, entity_id))
        audit("Dosya yüklendi", f"{entity}#{entity_id}: {name} ({kind})")
        saved += 1
    return saved


def attachments_for(entity, entity_id):
    rows = get_db().execute(
        "SELECT a.*, u.full_name FROM attachments a LEFT JOIN users u ON u.id=a.user_id "
        "WHERE entity=? AND entity_id=? ORDER BY a.id DESC", (entity, entity_id)).fetchall()
    out = []
    for r in rows:
        d = dict(r)
        ext = r["filename"].rsplit(".", 1)[-1].lower()
        d["is_image"] = ext in IMAGE_EXT
        d["size_txt"] = f"{r['size'] / 1024:.0f} KB" if r["size"] < 1024 ** 2 \
            else f"{r['size'] / 1024 ** 2:.1f} MB"
        out.append(d)
    return out


def can_view_attachment(a):
    """Fatura/teklif gibi fiyat içeren belgeler sadece yetkililere açık."""
    if a["entity"] in ("movement", "supplier") or a["kind"] in ("Fatura", "Teklif"):
        return can_any("can_see_price", "can_purchase", "can_in")
    return True


@app.route("/dosya/<int:aid>")
@login_required
def attachment_get(aid):
    a = get_db().execute("SELECT * FROM attachments WHERE id=?", (aid,)).fetchone() or abort(404)
    if not can_view_attachment(a):
        abort(403)
    path = os.path.normpath(os.path.join(UPLOAD_DIR, a["stored_name"]))
    if not path.startswith(os.path.normpath(UPLOAD_DIR)) or not os.path.exists(path):
        abort(404)
    ext = a["filename"].rsplit(".", 1)[-1].lower()
    inline = ext in INLINE_EXT and request.args.get("indir") != "1"
    return send_file(path, download_name=a["filename"], as_attachment=not inline,
                     mimetype=mimetypes.guess_type(a["filename"])[0] or "application/octet-stream")


@app.route("/dosya/<int:aid>/sil", methods=["POST"])
@login_required
def attachment_delete(aid):
    db = get_db()
    a = db.execute("SELECT * FROM attachments WHERE id=?", (aid,)).fetchone() or abort(404)
    if not (can("can_edit") or can("is_admin") or a["user_id"] == g.user["id"]):
        abort(403)
    db.execute("DELETE FROM attachments WHERE id=?", (aid,))
    db.execute("UPDATE products SET image_id=NULL WHERE image_id=?", (aid,))
    audit("Dosya silindi", f"{a['entity']}#{a['entity_id']}: {a['filename']}")
    db.commit()
    try:
        os.remove(os.path.join(UPLOAD_DIR, a["stored_name"]))
    except OSError:
        pass
    flash("Dosya silindi.", "ok")
    return redirect(request.referrer or url_for("index"))


@app.route("/dosya/yukle/<entity>/<int:eid>", methods=["POST"])
@login_required
def attachment_upload(entity, eid):
    perms = {"product": ("can_edit", "can_in", "can_purchase"),
             "supplier": ("can_purchase",),
             "movement": ("can_in", "can_purchase"),
             "request": ("can_request", "can_approve", "can_out"),
             "location": ("can_edit",)}
    if entity not in perms or not can_any(*perms[entity]):
        abort(403)
    n = save_attachments(entity, eid, request.files.getlist("files"), request.form.get("kind"))
    get_db().commit()
    if n:
        flash(f"{n} dosya yüklendi.", "ok")
    return redirect(request.referrer or url_for("index"))


@app.route("/urun/<int:pid>/kapak/<int:aid>", methods=["POST"])
@permission_required("can_edit")
def product_set_image(pid, aid):
    get_db().execute("UPDATE products SET image_id=? WHERE id=?", (aid, pid))
    get_db().commit()
    return redirect(url_for("product_detail", pid=pid))


# ============================================================ Giriş / Çıkış
@app.route("/login", methods=["GET", "POST"])
def login():
    if request.method == "POST":
        username = request.form.get("username", "").strip()
        password = request.form.get("password", "")
        db = get_db()
        user = db.execute("SELECT * FROM users WHERE username=?", (username,)).fetchone()
        if user and user["active"] and check_password_hash(user["password_hash"], password):
            csrf = session.get("csrf")
            session.clear()
            session.permanent = True
            session["uid"] = user["id"]
            session["csrf"] = csrf or secrets.token_hex(16)
            db.execute("UPDATE users SET last_login=? WHERE id=?", (now(), user["id"]))
            db.commit()
            nxt = request.args.get("next", "")
            if not nxt.startswith("/") or nxt.startswith("//"):
                nxt = url_for("index")
            return redirect(nxt)
        g.user = None
        audit("Hatalı giriş denemesi", username)
        db.commit()
        flash("Kullanıcı adı veya şifre hatalı ya da hesap pasif.", "error")
    return render_template("login.html")


@app.route("/logout")
def logout():
    session.clear()
    return redirect(url_for("login"))


@app.route("/sifre", methods=["GET", "POST"])
@login_required
def change_password():
    if request.method == "POST":
        old, new, new2 = (request.form.get(k, "") for k in ("old", "new", "new2"))
        if not check_password_hash(g.user["password_hash"], old):
            flash("Mevcut şifre hatalı.", "error")
        elif len(new) < 6:
            flash("Yeni şifre en az 6 karakter olmalı.", "error")
        elif new != new2:
            flash("Yeni şifreler eşleşmiyor.", "error")
        elif new == old:
            flash("Yeni şifre eskisiyle aynı olamaz.", "error")
        else:
            db = get_db()
            db.execute("UPDATE users SET password_hash=?, must_change_pw=0 WHERE id=?",
                       (generate_password_hash(new), g.user["id"]))
            db.commit()
            flash("Şifreniz değiştirildi.", "ok")
            return redirect(url_for("index"))
    return render_template("change_password.html")


# =============================================================== Ana Sayfa
MOVE_SELECT = (
    "SELECT m.*, p.code, p.name, p.unit, u.full_name, s.name AS supplier_name "
    "FROM movements m JOIN products p ON p.id=m.product_id JOIN users u ON u.id=m.user_id "
    "LEFT JOIN suppliers s ON s.id=m.supplier_id")


@app.route("/")
@login_required
def index():
    db = get_db()
    stats = db.execute(
        "SELECT COUNT(*) AS total,"
        " SUM(CASE WHEN quantity <= min_stock AND min_stock > 0 THEN 1 ELSE 0 END) AS low,"
        " SUM(CASE WHEN quantity <= 0 THEN 1 ELSE 0 END) AS empty"
        " FROM products WHERE active=1").fetchone()
    value = []
    if can("can_see_price"):
        value = db.execute("SELECT currency, SUM(quantity*last_price) v FROM products "
                           "WHERE active=1 AND quantity>0 GROUP BY currency").fetchall()
    low = db.execute(
        "SELECT p.*, s.name AS supplier_name FROM products p "
        "LEFT JOIN suppliers s ON s.id=p.default_supplier_id "
        "WHERE p.active=1 AND p.min_stock > 0 AND p.quantity <= p.min_stock "
        "ORDER BY (p.quantity / p.min_stock) LIMIT 30").fetchall()
    recent = db.execute(MOVE_SELECT + " ORDER BY m.id DESC LIMIT 12").fetchall()
    today = {r["type"]: r["c"] for r in db.execute(
        "SELECT type, COUNT(*) c FROM movements WHERE created_at >= date('now','localtime')"
        " GROUP BY type")}
    my_requests = db.execute(
        "SELECT * FROM requests WHERE requester_id=? AND status IN ('BEKLIYOR','ONAYLANDI','KISMI')"
        " ORDER BY id DESC LIMIT 10", (g.user["id"],)).fetchall()
    return render_template("index.html", stats=stats, low=low, recent=recent, today=today,
                           value=value, my_requests=my_requests)


@app.route("/ara")
@login_required
def global_search():
    """Barkod/QR okutma veya hızlı arama: ürün kodu, barkod veya lokasyon kodu."""
    q = request.args.get("q", "").strip()
    db = get_db()
    p = db.execute("SELECT id FROM products WHERE code=? OR (barcode<>'' AND barcode=?)",
                   (q, q)).fetchone()
    if p:
        return redirect(url_for("product_detail", pid=p["id"]))
    loc = db.execute("SELECT id FROM locations WHERE code=?", (q,)).fetchone()
    if loc:
        return redirect(url_for("location_detail", lid=loc["id"]))
    return redirect(url_for("products", q=q))


# =================================================================== Ürünler
@app.route("/urunler")
@login_required
def products():
    a = request.args
    q = a.get("q", "").strip()
    db = get_db()
    sql = ("SELECT p.*, c.name AS cat_name, s.name AS supplier_name FROM products p "
           "LEFT JOIN categories c ON c.id=p.category_id "
           "LEFT JOIN suppliers s ON s.id=p.default_supplier_id WHERE 1=1")
    params = []
    if a.get("pasif") != "1":
        sql += " AND p.active=1"
    if q:
        sql += (" AND (p.code LIKE ? OR p.name LIKE ? OR p.brand LIKE ? OR p.model LIKE ?"
                " OR p.description LIKE ? OR p.barcode LIKE ? OR p.tech_spec LIKE ?)")
        params += [f"%{q}%"] * 7
    if a.get("kategori", type=int):
        ids = subtree_ids("categories", a.get("kategori", type=int))
        sql += f" AND p.category_id IN ({','.join('?' * len(ids))})"
        params += ids
    if a.get("lokasyon", type=int):
        ids = subtree_ids("locations", a.get("lokasyon", type=int))
        sql += (f" AND p.id IN (SELECT product_id FROM stock WHERE qty<>0 AND location_id IN "
                f"({','.join('?' * len(ids))}))")
        params += ids
    if a.get("tedarikci", type=int):
        sql += " AND p.default_supplier_id=?"
        params.append(a.get("tedarikci", type=int))
    if a.get("marka"):
        sql += " AND p.brand=?"
        params.append(a["marka"])
    if a.get("kritik") == "1":
        sql += " AND p.min_stock > 0 AND p.quantity <= p.min_stock"
    sql += " ORDER BY p.code LIMIT 2000"
    rows = db.execute(sql, params).fetchall()
    if q and len(rows) == 1 and rows[0]["code"].lower() == q.lower():
        return redirect(url_for("product_detail", pid=rows[0]["id"]))
    brands = [r[0] for r in db.execute(
        "SELECT DISTINCT brand FROM products WHERE brand<>'' ORDER BY brand")]
    suppliers = db.execute("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name").fetchall()
    loc_paths = tree_paths("locations")
    # Her ürünün bulunduğu lokasyon kodları (listede göstermek için)
    where = {}
    for r in db.execute("SELECT s.product_id, l.code FROM stock s JOIN locations l "
                        "ON l.id=s.location_id WHERE s.qty<>0 ORDER BY l.code"):
        where.setdefault(r["product_id"], []).append(r["code"])
    if a.get("csv") == "1" and can("can_report"):
        hdr = ["Stok Kodu", "Ürün", "Marka", "Model", "Kategori", "Birim", "Miktar",
               "Min.", "Lokasyonlar", "Tedarikçi"]
        if can("can_see_price"):
            hdr += ["Son Fiyat", "Para Birimi", "Tutar"]
        out = []
        for r in rows:
            line = [r["code"], r["name"], r["brand"], r["model"], r["cat_name"] or "", r["unit"],
                    xl(r["quantity"]), xl(r["min_stock"]), ", ".join(where.get(r["id"], [])),
                    r["supplier_name"] or ""]
            if can("can_see_price"):
                line += [xl(r["last_price"]), r["currency"], xl(r["quantity"] * r["last_price"])]
            out.append(line)
        return csv_response("stok_listesi", hdr, out)
    return render_template("products.html", rows=rows, q=q, a=a, cats=category_options(),
                           locs=location_options(), brands=brands, suppliers=suppliers,
                           where=where, loc_paths=loc_paths)


def product_form_data():
    f = request.form
    data = {k: f.get(k, "").strip() for k in
            ("code", "name", "description", "brand", "model", "barcode", "tech_spec", "unit",
             "currency")}
    data["unit"] = data["unit"] or "adet"
    data["currency"] = data["currency"] if data["currency"] in CURRENCIES else "TRY"
    data["min_stock"] = parse_num(f.get("min_stock"), 0)
    data["max_stock"] = parse_num(f.get("max_stock"), 0)
    data["category_id"] = f.get("category_id", type=int)
    data["default_location_id"] = f.get("default_location_id", type=int)
    data["default_supplier_id"] = f.get("default_supplier_id", type=int)
    errors = []
    if not data["code"]:
        errors.append("Stok kodu zorunlu.")
    if not data["name"]:
        errors.append("Ürün adı zorunlu.")
    for k, lbl in (("min_stock", "Minimum stok"), ("max_stock", "Maksimum stok")):
        if data[k] is None or data[k] < 0:
            errors.append(f"{lbl} geçerli bir sayı olmalı.")
    if can("can_see_price"):
        data["last_price"] = parse_num(f.get("last_price"), 0)
        if data["last_price"] is None or data["last_price"] < 0:
            errors.append("Fiyat geçerli bir sayı olmalı.")
    return data, errors


def product_form_ctx():
    db = get_db()
    return {"cats": category_options(), "locs": location_options(),
            "suppliers": db.execute("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name"
                                    ).fetchall(),
            "brands": [r[0] for r in db.execute(
                "SELECT DISTINCT brand FROM products WHERE brand<>'' ORDER BY brand")],
            "units": sorted({"adet", "kg", "lt", "m", "m²", "paket", "koli", "takım", "rulo",
                             "çift", "ton"} | {r[0] for r in db.execute(
                                 "SELECT DISTINCT unit FROM products")}),
            "CURRENCIES": CURRENCIES, "ATTACH_KINDS": ATTACH_KINDS}


@app.route("/urun/yeni", methods=["GET", "POST"])
@permission_required("can_edit")
def product_new():
    db = get_db()
    data = {"code": "", "name": "", "unit": "adet", "currency": "TRY", "min_stock": 0,
            "max_stock": 0, "last_price": 0}
    if request.args.get("kopya", type=int):  # Benzer ürün kopyalama
        src = db.execute("SELECT * FROM products WHERE id=?",
                         (request.args.get("kopya", type=int),)).fetchone()
        if src:
            data = dict(src, code="", barcode="", id=None)
    if request.method == "POST":
        data, errors = product_form_data()
        initial = parse_num(request.form.get("initial"), 0)
        if initial is None or initial < 0:
            errors.append("Başlangıç miktarı geçerli bir sayı olmalı.")
        if initial and not data["default_location_id"]:
            errors.append("Başlangıç miktarı için varsayılan lokasyon seçin.")
        if not errors:
            try:
                cols = ["code", "name", "description", "brand", "model", "barcode", "tech_spec",
                        "unit", "currency", "min_stock", "max_stock", "category_id",
                        "default_location_id", "default_supplier_id"]
                if "last_price" in data:
                    cols.append("last_price")
                cur = db.execute(
                    f"INSERT INTO products ({','.join(cols)}, created_by, created_at, updated_at)"
                    f" VALUES ({','.join('?' * len(cols))},?,?,?)",
                    [data[c] for c in cols] + [g.user["id"], now(), now()])
                pid = cur.lastrowid
                if initial:
                    apply_move(db, pid, "SAYIM", initial, None, data["default_location_id"],
                               note="Açılış stoğu")
                save_attachments("product", pid, request.files.getlist("files"),
                                 request.form.get("kind"))
                audit("Ürün oluşturuldu", f"{data['code']} - {data['name']}")
                db.commit()
                flash(f"{data['code']} eklendi.", "ok")
                return redirect(url_for("product_detail", pid=pid))
            except sqlite3.IntegrityError:
                db.rollback()
                errors.append("Bu stok kodu zaten kayıtlı.")
        for e in errors:
            flash(e, "error")
    return render_template("product_form.html", p=data, new=True, **product_form_ctx())


@app.route("/urun/<int:pid>/duzenle", methods=["GET", "POST"])
@permission_required("can_edit")
def product_edit(pid):
    db = get_db()
    p = db.execute("SELECT * FROM products WHERE id=?", (pid,)).fetchone() or abort(404)
    if request.method == "POST":
        data, errors = product_form_data()
        data["active"] = 1 if request.form.get("active") else 0
        if not errors:
            cols = ["code", "name", "description", "brand", "model", "barcode", "tech_spec",
                    "unit", "currency", "min_stock", "max_stock", "category_id",
                    "default_location_id", "default_supplier_id", "active"]
            if "last_price" in data:
                cols.append("last_price")
            changes = [f"{c}: '{p[c]}' → '{data[c]}'" for c in cols
                       if str(p[c] if p[c] is not None else "") !=
                       str(data[c] if data[c] is not None else "")]
            try:
                db.execute(f"UPDATE products SET {', '.join(c + '=?' for c in cols)}, "
                           f"updated_at=? WHERE id=?",
                           [data[c] for c in cols] + [now(), pid])
                if changes:
                    audit("Ürün güncellendi", f"{p['code']}: " + "; ".join(changes))
                db.commit()
                flash("Ürün güncellendi.", "ok")
                return redirect(url_for("product_detail", pid=pid))
            except sqlite3.IntegrityError:
                db.rollback()
                errors.append("Bu stok kodu başka bir üründe kayıtlı.")
        for e in errors:
            flash(e, "error")
        p = dict(p, **data)
    return render_template("product_form.html", p=p, new=False, **product_form_ctx())


@app.route("/urun/<int:pid>")
@login_required
def product_detail(pid):
    db = get_db()
    p = db.execute(
        "SELECT p.*, s.name AS supplier_name, u.full_name AS creator FROM products p "
        "LEFT JOIN suppliers s ON s.id=p.default_supplier_id "
        "LEFT JOIN users u ON u.id=p.created_by WHERE p.id=?", (pid,)).fetchone() or abort(404)
    loc_paths = tree_paths("locations")
    stocks = db.execute("SELECT s.*, l.code FROM stock s JOIN locations l ON l.id=s.location_id "
                        "WHERE s.product_id=? AND s.qty<>0 ORDER BY l.code", (pid,)).fetchall()
    moves = db.execute(MOVE_SELECT + " WHERE m.product_id=? ORDER BY m.id DESC LIMIT 100",
                       (pid,)).fetchall()
    purchases = []
    if can("can_see_price"):
        purchases = db.execute(
            "SELECT m.created_at, m.qty, m.unit_price, m.currency, m.document_no, "
            "s.name AS supplier_name FROM movements m LEFT JOIN suppliers s ON s.id=m.supplier_id "
            "WHERE m.product_id=? AND m.type='GIRIS' AND m.unit_price IS NOT NULL "
            "ORDER BY m.id DESC LIMIT 20", (pid,)).fetchall()
    usage = db.execute(
        "SELECT project, SUM(qty) q FROM movements WHERE product_id=? AND type='CIKIS' "
        "AND created_at >= date('now','-365 day') GROUP BY project ORDER BY q DESC LIMIT 10",
        (pid,)).fetchall()
    open_req = db.execute(
        "SELECT r.id, r.created_at, r.status, ri.qty - ri.issued_qty AS rem, u.full_name "
        "FROM request_items ri JOIN requests r ON r.id=ri.request_id "
        "JOIN users u ON u.id=r.requester_id WHERE ri.product_id=? AND "
        "r.status IN ('BEKLIYOR','ONAYLANDI','KISMI') AND ri.qty > ri.issued_qty", (pid,)).fetchall()
    return render_template(
        "product_detail.html", p=p, stocks=stocks, moves=moves, loc_paths=loc_paths,
        cat_path=tree_paths("categories").get(p["category_id"], ""),
        attachments=[a for a in attachments_for("product", pid) if can_view_attachment(a)],
        purchases=purchases, usage=usage, open_req=open_req, ATTACH_KINDS=ATTACH_KINDS)


# ============================================================ Stok Hareketi
class StockError(Exception):
    pass


def apply_move(db, pid, kind, qty, from_loc, to_loc, **meta):
    """Tek bir stok hareketini uygular. Çağıran taraf commit/rollback yapar."""
    if not db.in_transaction:
        db.execute("BEGIN IMMEDIATE")  # eşzamanlı işlemlere karşı yazma kilidi
    p = db.execute("SELECT * FROM products WHERE id=?", (pid,)).fetchone()
    if not p or not p["active"]:
        raise StockError("Ürün bulunamadı veya pasif.")

    def loc_qty(lid):
        r = db.execute("SELECT qty FROM stock WHERE product_id=? AND location_id=?",
                       (pid, lid)).fetchone()
        return r["qty"] if r else 0.0

    def set_qty(lid, q):
        db.execute("INSERT INTO stock (product_id, location_id, qty) VALUES (?,?,?) "
                   "ON CONFLICT(product_id, location_id) DO UPDATE SET qty=excluded.qty",
                   (pid, lid, round(q, 6)))

    for lid in (from_loc, to_loc):
        if lid and not db.execute("SELECT 1 FROM locations WHERE id=? AND active=1",
                                  (lid,)).fetchone():
            raise StockError("Seçilen lokasyon bulunamadı veya pasif.")

    if kind == "GIRIS":
        if not to_loc:
            raise StockError("Giriş lokasyonu seçin.")
        if qty <= 0:
            raise StockError("Miktar sıfırdan büyük olmalı.")
        set_qty(to_loc, loc_qty(to_loc) + qty)
        delta = qty
    elif kind == "CIKIS":
        if not from_loc:
            raise StockError("Çıkış yapılacak lokasyonu seçin.")
        if qty <= 0:
            raise StockError("Miktar sıfırdan büyük olmalı.")
        have = loc_qty(from_loc)
        if have + 1e-9 < qty:
            raise StockError(f"{p['code']}: Bu lokasyonda yetersiz stok! Mevcut "
                             f"{fmt_qty(have)} {p['unit']}, istenen {fmt_qty(qty)}.")
        set_qty(from_loc, have - qty)
        delta = -qty
    elif kind == "TRANSFER":
        if not from_loc or not to_loc or from_loc == to_loc:
            raise StockError("Transfer için farklı kaynak ve hedef lokasyon seçin.")
        if qty <= 0:
            raise StockError("Miktar sıfırdan büyük olmalı.")
        have = loc_qty(from_loc)
        if have + 1e-9 < qty:
            raise StockError(f"Kaynak lokasyonda yetersiz stok (mevcut {fmt_qty(have)}).")
        set_qty(from_loc, have - qty)
        set_qty(to_loc, loc_qty(to_loc) + qty)
        delta = 0
    elif kind == "SAYIM":
        if not to_loc:
            raise StockError("Sayım lokasyonu seçin.")
        if qty < 0:
            raise StockError("Sayım miktarı negatif olamaz.")
        delta = qty - loc_qty(to_loc)
        set_qty(to_loc, qty)
        qty = round(delta, 6)
    else:
        raise StockError("Geçersiz hareket tipi.")

    before = p["quantity"]
    after = round(before + delta, 6)
    db.execute("UPDATE products SET quantity=?, updated_at=? WHERE id=?", (after, now(), pid))
    if kind == "GIRIS" and meta.get("unit_price") is not None:
        db.execute("UPDATE products SET last_price=?, currency=? WHERE id=?",
                   (meta["unit_price"], meta.get("currency") or "TRY", pid))
    if kind == "GIRIS" and meta.get("supplier_id") and not p["default_supplier_id"]:
        db.execute("UPDATE products SET default_supplier_id=? WHERE id=?",
                   (meta["supplier_id"], pid))
    if kind in ("GIRIS", "SAYIM") and to_loc and not p["default_location_id"]:
        db.execute("UPDATE products SET default_location_id=? WHERE id=?", (to_loc, pid))
    cur = db.execute(
        "INSERT INTO movements (product_id, user_id, type, qty, from_location_id, to_location_id,"
        " qty_before, qty_after, supplier_id, unit_price, currency, document_no, document_date,"
        " project, receiver, request_id, note, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (pid, g.user["id"], kind, qty, from_loc if kind in ("CIKIS", "TRANSFER") else None,
         to_loc if kind != "CIKIS" else None, before, after, meta.get("supplier_id"),
         meta.get("unit_price"), meta.get("currency") if meta.get("unit_price") is not None
         else None, meta.get("document_no", ""), meta.get("document_date", ""),
         meta.get("project", ""), meta.get("receiver", ""), meta.get("request_id"),
         meta.get("note", ""), now()))
    return cur.lastrowid, after, p


MOVE_KIND = {"giris": ("GIRIS", "can_in"), "cikis": ("CIKIS", "can_out"),
             "transfer": ("TRANSFER", "can_transfer"), "sayim": ("SAYIM", "can_count")}


@app.route("/hareket/<kind>", methods=["GET", "POST"])
@login_required
def movement(kind):
    if kind not in MOVE_KIND:
        abort(404)
    mtype, perm = MOVE_KIND[kind]
    if not can(perm):
        abort(403)
    db = get_db()
    pre = None
    if request.args.get("pid", type=int):
        pre = db.execute("SELECT code FROM products WHERE id=?",
                         (request.args.get("pid", type=int),)).fetchone()
    if request.method == "POST":
        f = request.form
        code = f.get("code", "").strip()
        product = db.execute("SELECT * FROM products WHERE (code=? OR (barcode<>'' AND barcode=?))"
                             " AND active=1", (code, code)).fetchone()
        qty = parse_num(f.get("qty"))
        price = parse_num(f.get("unit_price")) if can("can_see_price") or can("can_in") else None
        try:
            if not product:
                raise StockError("Ürün bulunamadı. Stok kodunu kontrol edin.")
            if qty is None:
                raise StockError("Miktar geçerli bir sayı olmalı.")
            if mtype == "SAYIM" and not f.get("note", "").strip():
                raise StockError("Sayım düzeltmesinde açıklama zorunludur.")
            if price is not None and price < 0:
                raise StockError("Birim fiyat negatif olamaz.")
            mid, after, p = apply_move(
                db, product["id"], mtype, qty, f.get("from_location_id", type=int),
                f.get("to_location_id", type=int),
                supplier_id=f.get("supplier_id", type=int) if mtype == "GIRIS" else None,
                unit_price=price if mtype == "GIRIS" else None,
                currency=f.get("currency") if f.get("currency") in CURRENCIES else "TRY",
                document_no=f.get("document_no", "").strip(),
                document_date=f.get("document_date", "").strip(),
                project=f.get("project", "").strip(), receiver=f.get("receiver", "").strip(),
                note=f.get("note", "").strip())
            save_attachments("movement", mid, request.files.getlist("files"),
                             f.get("kind") or "Fatura")
            db.commit()
            flash(f"{p['code']} – {MOVE_LABEL[mtype]} kaydedildi. Toplam stok: "
                  f"{fmt_qty(after)} {p['unit']}", "ok")
            if f.get("again"):
                keep = {k: f.get(k) for k in ("document_no", "document_date", "supplier_id",
                                              "project", "receiver", "to_location_id")
                        if f.get(k)}
                return redirect(url_for("movement", kind=kind, **keep))
            return redirect(url_for("product_detail", pid=p["id"]))
        except StockError as e:
            db.rollback()
            flash(str(e), "error")
    return render_template(
        "movement.html", kind=kind, mtype=mtype, pre_code=pre["code"] if pre else "",
        locs=location_options(), CURRENCIES=CURRENCIES, ATTACH_KINDS=ATTACH_KINDS,
        suppliers=db.execute("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name"
                             ).fetchall(),
        projects=[r[0] for r in db.execute(
            "SELECT DISTINCT project FROM movements WHERE project<>'' ORDER BY id DESC LIMIT 50")])


@app.route("/hareket/detay/<int:mid>")
@login_required
def movement_detail(mid):
    db = get_db()
    m = db.execute(MOVE_SELECT + " WHERE m.id=?", (mid,)).fetchone() or abort(404)
    paths = tree_paths("locations")
    atts = [a for a in attachments_for("movement", mid) if can_view_attachment(a)]
    return render_template("movement_detail.html", m=m, paths=paths, attachments=atts,
                           ATTACH_KINDS=ATTACH_KINDS)


@app.route("/api/urun")
@login_required
def api_product_lookup():
    code = request.args.get("code", "").strip()
    db = get_db()
    p = db.execute("SELECT * FROM products WHERE (code=? OR (barcode<>'' AND barcode=?)) "
                   "AND active=1", (code, code)).fetchone()
    if not p:
        return {"found": False}
    paths = tree_paths("locations")
    stocks = [{"id": r["location_id"], "path": paths.get(r["location_id"], "?"),
               "qty": r["qty"], "qty_txt": fmt_qty(r["qty"])}
              for r in db.execute("SELECT * FROM stock WHERE product_id=? AND qty<>0",
                                  (p["id"],))]
    stocks.sort(key=lambda s: s["path"])
    d = {"found": True, "id": p["id"], "code": p["code"], "name": p["name"], "unit": p["unit"],
         "brand": p["brand"], "model": p["model"], "quantity": fmt_qty(p["quantity"]),
         "default_location_id": p["default_location_id"],
         "default_supplier_id": p["default_supplier_id"], "stocks": stocks}
    if can("can_see_price"):
        d.update(last_price=fmt_qty(p["last_price"]) if p["last_price"] else "",
                 currency=p["currency"])
    return d


@app.route("/api/ara")
@login_required
def api_search():
    q = request.args.get("q", "").strip()
    if len(q) < 2:
        return {"items": []}
    rows = get_db().execute(
        "SELECT code, name, brand, model, unit, quantity FROM products WHERE active=1 AND "
        "(code LIKE ? OR name LIKE ? OR brand LIKE ? OR model LIKE ?) ORDER BY code LIMIT 20",
        [f"%{q}%"] * 4).fetchall()
    return {"items": [{"code": r["code"], "label": f"{r['name']} {r['brand']} {r['model']}"
                       f" ({fmt_qty(r['quantity'])} {r['unit']})".replace("  ", " ")}
                      for r in rows]}


# ============================================================= Hareket listesi
def movement_query():
    a = request.args
    sql = MOVE_SELECT + " WHERE 1=1"
    params = []
    if a.get("bas"):
        sql += " AND m.created_at >= ?"
        params.append(a["bas"])
    if a.get("bit"):
        sql += " AND m.created_at < date(?, '+1 day')"
        params.append(a["bit"])
    if a.get("tip") in MOVE_LABEL:
        sql += " AND m.type=?"
        params.append(a["tip"])
    if a.get("q"):
        sql += (" AND (p.code LIKE ? OR p.name LIKE ? OR m.document_no LIKE ? OR m.project LIKE ?"
                " OR m.note LIKE ? OR m.receiver LIKE ?)")
        params += [f"%{a['q']}%"] * 6
    if a.get("kullanici", type=int):
        sql += " AND m.user_id=?"
        params.append(a.get("kullanici", type=int))
    if a.get("tedarikci", type=int):
        sql += " AND m.supplier_id=?"
        params.append(a.get("tedarikci", type=int))
    if a.get("lokasyon", type=int):
        ids = subtree_ids("locations", a.get("lokasyon", type=int))
        ph = ",".join("?" * len(ids))
        sql += f" AND (m.from_location_id IN ({ph}) OR m.to_location_id IN ({ph}))"
        params += ids + ids
    sql += " ORDER BY m.id DESC"
    return sql, params


@app.route("/hareketler")
@permission_required("can_report", "can_in", "can_out")
def movements():
    sql, params = movement_query()
    db = get_db()
    paths = tree_paths("locations")
    if request.args.get("csv") == "1":
        rows = db.execute(sql, params).fetchall()
        hdr = ["Tarih", "Tip", "Stok Kodu", "Ürün", "Miktar", "Birim", "Nereden", "Nereye",
               "Toplam Önce", "Toplam Sonra", "Tedarikçi", "Belge No", "Belge Tarihi",
               "Proje/İş Emri/Makine", "Teslim Alan", "Talep No", "Açıklama", "Kullanıcı"]
        if can("can_see_price"):
            hdr += ["Birim Fiyat", "Para Birimi", "Tutar"]
        out = []
        for r in rows:
            line = [r["created_at"], MOVE_LABEL[r["type"]], r["code"], r["name"], xl(r["qty"]),
                    r["unit"], paths.get(r["from_location_id"], ""),
                    paths.get(r["to_location_id"], ""), xl(r["qty_before"]), xl(r["qty_after"]),
                    r["supplier_name"] or "", r["document_no"], r["document_date"], r["project"],
                    r["receiver"], req_no(r["request_id"]) if r["request_id"] else "",
                    r["note"], r["full_name"]]
            if can("can_see_price"):
                line += [xl(r["unit_price"]), r["currency"] or "",
                         xl(r["unit_price"] * r["qty"]) if r["unit_price"] is not None else ""]
            out.append(line)
        return csv_response("hareketler", hdr, out)
    rows = db.execute(sql + " LIMIT 500", params).fetchall()
    return render_template(
        "movements.html", rows=rows, a=request.args, paths=paths, locs=location_options(False),
        users=db.execute("SELECT id, full_name FROM users ORDER BY full_name").fetchall(),
        suppliers=db.execute("SELECT id, name FROM suppliers ORDER BY name").fetchall())


def csv_response(name, header, rows):
    buf = io.StringIO()
    buf.write("﻿")  # Excel Türkçe karakterleri doğru açsın
    w = csv.writer(buf, delimiter=";")
    w.writerow(header)
    w.writerows(rows)
    fn = f"{name}_{datetime.now():%Y%m%d_%H%M}.csv"
    return Response(buf.getvalue(), mimetype="text/csv; charset=utf-8",
                    headers={"Content-Disposition": f"attachment; filename={fn}"})


# =========================================================== Malzeme Talebi
@app.route("/talepler")
@login_required
def requests_list():
    db = get_db()
    st = request.args.get("durum", "")
    see_all = can_any("can_approve", "can_out", "can_report")
    sql = ("SELECT r.*, u.full_name, a.full_name AS approver, "
           "(SELECT COUNT(*) FROM request_items WHERE request_id=r.id) AS n "
           "FROM requests r JOIN users u ON u.id=r.requester_id "
           "LEFT JOIN users a ON a.id=r.approver_id WHERE 1=1")
    params = []
    if not see_all or request.args.get("benim") == "1":
        sql += " AND r.requester_id=?"
        params.append(g.user["id"])
    if st == "acik":
        sql += " AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI')"
    elif st in REQ_STATUS:
        sql += " AND r.status=?"
        params.append(st)
    if request.args.get("q"):
        sql += " AND (r.project LIKE ? OR r.note LIKE ? OR u.full_name LIKE ?)"
        params += [f"%{request.args['q']}%"] * 3
    sql += " ORDER BY CASE r.status WHEN 'BEKLIYOR' THEN 0 WHEN 'ONAYLANDI' THEN 1 " \
           "WHEN 'KISMI' THEN 2 ELSE 3 END, r.id DESC LIMIT 300"
    return render_template("requests.html", rows=db.execute(sql, params).fetchall(),
                           a=request.args, see_all=see_all)


@app.route("/talep/yeni", methods=["GET", "POST"])
@permission_required("can_request")
def request_new():
    db = get_db()
    if request.method == "POST":
        f = request.form
        codes, qtys, notes = f.getlist("code"), f.getlist("qty"), f.getlist("item_note")
        items, errors = [], []
        for i, code in enumerate(codes):
            code = code.strip()
            if not code:
                continue
            p = db.execute("SELECT id, code FROM products WHERE (code=? OR "
                           "(barcode<>'' AND barcode=?)) AND active=1", (code, code)).fetchone()
            q = parse_num(qtys[i] if i < len(qtys) else "")
            if not p:
                errors.append(f"'{code}' kodlu ürün bulunamadı.")
            elif q is None or q <= 0:
                errors.append(f"{code}: miktar geçersiz.")
            else:
                items.append((p["id"], q, (notes[i] if i < len(notes) else "").strip()))
        if not items and not errors:
            errors.append("En az bir ürün ekleyin.")
        if not f.get("project", "").strip():
            errors.append("Proje / İş emri / Makine alanı zorunlu.")
        if not errors:
            cur = db.execute(
                "INSERT INTO requests (requester_id, department, project, needed_date, priority,"
                " note, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?)",
                (g.user["id"], g.user["department"], f.get("project", "").strip(),
                 f.get("needed_date", ""), f.get("priority", "Normal"),
                 f.get("note", "").strip(), now(), now()))
            rid = cur.lastrowid
            for pid, q, n in items:
                db.execute("INSERT INTO request_items (request_id, product_id, qty, note) "
                           "VALUES (?,?,?,?)", (rid, pid, q, n))
            save_attachments("request", rid, request.files.getlist("files"), "Teknik Resim")
            audit("Malzeme talebi oluşturuldu", req_no(rid))
            db.commit()
            flash(f"{req_no(rid)} numaralı talep oluşturuldu, onaya gönderildi.", "ok")
            return redirect(url_for("request_detail", rid=rid))
        for e in errors:
            flash(e, "error")
    return render_template("request_form.html", projects=[r[0] for r in db.execute(
        "SELECT DISTINCT project FROM requests ORDER BY id DESC LIMIT 50")])


def load_request(rid):
    db = get_db()
    r = db.execute("SELECT r.*, u.full_name, u.department AS req_dept, a.full_name AS approver "
                   "FROM requests r JOIN users u ON u.id=r.requester_id "
                   "LEFT JOIN users a ON a.id=r.approver_id WHERE r.id=?", (rid,)).fetchone()
    if not r:
        abort(404)
    if not (r["requester_id"] == g.user["id"] or can_any("can_approve", "can_out", "can_report")):
        abort(403)
    items = db.execute("SELECT ri.*, p.code, p.name, p.unit, p.quantity, p.brand, p.model "
                       "FROM request_items ri JOIN products p ON p.id=ri.product_id "
                       "WHERE ri.request_id=?", (rid,)).fetchall()
    return r, items


@app.route("/talep/<int:rid>")
@login_required
def request_detail(rid):
    r, items = load_request(rid)
    db = get_db()
    paths = tree_paths("locations")
    stocks = {}
    for it in items:
        stocks[it["product_id"]] = [
            {"id": s["location_id"], "path": paths.get(s["location_id"], "?"), "qty": s["qty"]}
            for s in db.execute("SELECT * FROM stock WHERE product_id=? AND qty>0 "
                                "ORDER BY qty DESC", (it["product_id"],))]
    moves = db.execute(MOVE_SELECT + " WHERE m.request_id=? ORDER BY m.id", (rid,)).fetchall()
    return render_template("request_detail.html", r=r, items=items, stocks=stocks, moves=moves,
                           paths=paths, attachments=attachments_for("request", rid),
                           ATTACH_KINDS=ATTACH_KINDS)


@app.route("/talep/<int:rid>/karar", methods=["POST"])
@permission_required("can_approve")
def request_decide(rid):
    db = get_db()
    r, _ = load_request(rid)
    if r["status"] != "BEKLIYOR":
        flash("Bu talep artık onay beklemiyor.", "error")
        return redirect(url_for("request_detail", rid=rid))
    decision = request.form.get("decision")
    if decision not in ("ONAYLANDI", "REDDEDILDI"):
        abort(400)
    note = request.form.get("decision_note", "").strip()
    if decision == "REDDEDILDI" and not note:
        flash("Red gerekçesi yazın.", "error")
        return redirect(url_for("request_detail", rid=rid))
    db.execute("UPDATE requests SET status=?, approver_id=?, approved_at=?, decision_note=?, "
               "updated_at=? WHERE id=?", (decision, g.user["id"], now(), note, now(), rid))
    audit(f"Talep {REQ_STATUS[decision]}", req_no(rid, r["created_at"]))
    db.commit()
    flash(f"Talep {REQ_STATUS[decision].lower()}.", "ok")
    return redirect(url_for("request_detail", rid=rid))


@app.route("/talep/<int:rid>/iptal", methods=["POST"])
@login_required
def request_cancel(rid):
    db = get_db()
    r, _ = load_request(rid)
    if r["requester_id"] != g.user["id"] and not can("is_admin"):
        abort(403)
    if r["status"] not in ("BEKLIYOR", "ONAYLANDI"):
        flash("Bu talep iptal edilemez.", "error")
    else:
        db.execute("UPDATE requests SET status='IPTAL', updated_at=? WHERE id=?", (now(), rid))
        audit("Talep iptal edildi", req_no(rid, r["created_at"]))
        db.commit()
        flash("Talep iptal edildi.", "ok")
    return redirect(url_for("request_detail", rid=rid))


@app.route("/talep/<int:rid>/teslim", methods=["POST"])
@permission_required("can_out")
def request_issue(rid):
    """Ambar, onaylı talebi lokasyon seçerek karşılar (kısmi teslim desteklenir)."""
    db = get_db()
    r, items = load_request(rid)
    if r["status"] not in ("ONAYLANDI", "KISMI"):
        flash("Sadece onaylanmış talepler karşılanabilir.", "error")
        return redirect(url_for("request_detail", rid=rid))
    f = request.form
    receiver = f.get("receiver", "").strip() or r["full_name"]
    done = 0
    try:
        for it in items:
            q = parse_num(f.get(f"qty_{it['id']}"))
            loc = f.get(f"loc_{it['id']}", type=int)
            if not q:
                continue
            remaining = it["qty"] - it["issued_qty"]
            if q > remaining + 1e-9:
                raise StockError(f"{it['code']}: kalan talep miktarından ({fmt_qty(remaining)}) "
                                 "fazla teslim edilemez.")
            apply_move(db, it["product_id"], "CIKIS", q, loc, None, project=r["project"],
                       receiver=receiver, request_id=rid,
                       note=f"Talep {req_no(rid, r['created_at'])}")
            db.execute("UPDATE request_items SET issued_qty=issued_qty+? WHERE id=?",
                       (q, it["id"]))
            done += 1
        if not done:
            raise StockError("Teslim edilecek miktar girilmedi.")
        left = db.execute("SELECT COUNT(*) FROM request_items WHERE request_id=? AND "
                          "issued_qty + 1e-9 < qty", (rid,)).fetchone()[0]
        status = "KISMI" if left else "TAMAMLANDI"
        db.execute("UPDATE requests SET status=?, updated_at=? WHERE id=?", (status, now(), rid))
        audit("Talep karşılandı", f"{req_no(rid, r['created_at'])} → {REQ_STATUS[status]}")
        db.commit()
        flash(f"Teslim kaydedildi. Durum: {REQ_STATUS[status]}", "ok")
    except StockError as e:
        db.rollback()
        flash(str(e), "error")
    return redirect(url_for("request_detail", rid=rid))


@app.route("/talep/<int:rid>/kapat", methods=["POST"])
@permission_required("can_out", "can_approve")
def request_close(rid):
    """Kısmi teslim edilmiş talebi kalan miktar olmadan kapatır."""
    db = get_db()
    r, _ = load_request(rid)
    if r["status"] == "KISMI":
        db.execute("UPDATE requests SET status='TAMAMLANDI', updated_at=? WHERE id=?",
                   (now(), rid))
        audit("Talep kapatıldı (kısmi)", req_no(rid, r["created_at"]))
        db.commit()
    return redirect(url_for("request_detail", rid=rid))


# =================================================================== Lokasyonlar
@app.route("/lokasyonlar")
@login_required
def locations():
    db = get_db()
    rows = db.execute(
        "SELECT l.*, (SELECT COUNT(*) FROM stock s WHERE s.location_id=l.id AND s.qty<>0) AS n "
        "FROM locations l").fetchall()
    children = {}
    for r in rows:
        children.setdefault(r["parent_id"], []).append(r)
    for v in children.values():
        v.sort(key=lambda r: r["code"])
    return render_template("locations.html", children=children)


@app.route("/lokasyon/<int:lid>")
@login_required
def location_detail(lid):
    db = get_db()
    loc = db.execute("SELECT * FROM locations WHERE id=?", (lid,)).fetchone() or abort(404)
    paths = tree_paths("locations")
    ids = subtree_ids("locations", lid)
    ph = ",".join("?" * len(ids))
    items = db.execute(
        f"SELECT s.*, p.code, p.name, p.unit, p.brand, p.model, l.code AS loc_code "
        f"FROM stock s JOIN products p ON p.id=s.product_id JOIN locations l ON l.id=s.location_id "
        f"WHERE s.location_id IN ({ph}) AND s.qty<>0 ORDER BY l.code, p.code", ids).fetchall()
    subs = db.execute("SELECT * FROM locations WHERE parent_id=? ORDER BY code", (lid,)).fetchall()
    return render_template("location_detail.html", loc=loc, path=paths[lid], items=items,
                           subs=subs, attachments=attachments_for("location", lid),
                           ATTACH_KINDS=ATTACH_KINDS)


@app.route("/lokasyon/yeni", methods=["GET", "POST"])
@app.route("/lokasyon/<int:lid>/duzenle", methods=["GET", "POST"])
@permission_required("can_edit")
def location_edit(lid=None):
    db = get_db()
    loc = db.execute("SELECT * FROM locations WHERE id=?", (lid,)).fetchone() if lid else None
    if lid and not loc:
        abort(404)
    data = dict(loc) if loc else {"parent_id": request.args.get("ust", type=int), "type": "Raf",
                                  "code": "", "name": "", "description": "", "active": 1}
    if request.method == "POST":
        f = request.form
        data = {"code": f.get("code", "").strip().upper(), "name": f.get("name", "").strip(),
                "type": f.get("type", "Raf"), "description": f.get("description", "").strip(),
                "parent_id": f.get("parent_id", type=int) or None,
                "active": 1 if f.get("active") or not lid else 0}
        errors = []
        if not data["code"] or not data["name"]:
            errors.append("Kod ve ad zorunlu.")
        if lid and data["parent_id"] and data["parent_id"] in subtree_ids("locations", lid):
            errors.append("Bir lokasyon kendi altına taşınamaz.")
        if lid and not data["active"] and db.execute(
                "SELECT 1 FROM stock WHERE location_id=? AND qty<>0", (lid,)).fetchone():
            errors.append("İçinde stok olan lokasyon pasif yapılamaz. Önce transfer edin.")
        if not errors:
            try:
                if lid:
                    db.execute("UPDATE locations SET code=?, name=?, type=?, description=?, "
                               "parent_id=?, active=? WHERE id=?",
                               (data["code"], data["name"], data["type"], data["description"],
                                data["parent_id"], data["active"], lid))
                else:
                    lid = db.execute("INSERT INTO locations (code, name, type, description, "
                                     "parent_id) VALUES (?,?,?,?,?)",
                                     (data["code"], data["name"], data["type"],
                                      data["description"], data["parent_id"])).lastrowid
                audit("Lokasyon kaydedildi", f"{data['code']} - {data['name']}")
                db.commit()
                flash("Lokasyon kaydedildi.", "ok")
                return redirect(url_for("location_detail", lid=lid))
            except sqlite3.IntegrityError:
                db.rollback()
                errors.append("Bu lokasyon kodu zaten var.")
        for e in errors:
            flash(e, "error")
    return render_template("location_form.html", loc=data, lid=lid, LOC_TYPES=LOC_TYPES,
                           locs=[o for o in location_options(False)
                                 if not lid or o[0] not in subtree_ids("locations", lid)])


@app.route("/lokasyon/toplu", methods=["POST"])
@permission_required("can_edit")
def location_bulk():
    """Bir lokasyonun altına seri raf/göz üretir: ör. önek R, 1..10 -> R01..R10"""
    db = get_db()
    parent = request.form.get("parent_id", type=int)
    pl = db.execute("SELECT * FROM locations WHERE id=?", (parent,)).fetchone() or abort(404)
    prefix = request.form.get("prefix", "").strip().upper()
    a, b = request.form.get("start", type=int), request.form.get("end", type=int)
    typ = request.form.get("type", "Göz")
    if not prefix or a is None or b is None or b < a or b - a > 200:
        flash("Geçersiz seri (en fazla 200 adet).", "error")
        return redirect(url_for("location_detail", lid=parent))
    n = 0
    width = max(2, len(str(b)))
    for i in range(a, b + 1):
        code = f"{pl['code']}-{prefix}{i:0{width}d}"
        try:
            db.execute("INSERT INTO locations (code, name, type, parent_id) VALUES (?,?,?,?)",
                       (code, f"{typ} {prefix}{i:0{width}d}", typ, parent))
            n += 1
        except sqlite3.IntegrityError:
            pass
    audit("Toplu lokasyon", f"{pl['code']} altına {n} adet")
    db.commit()
    flash(f"{n} lokasyon oluşturuldu.", "ok")
    return redirect(url_for("location_detail", lid=parent))


# =================================================================== Kategoriler
@app.route("/kategoriler", methods=["GET", "POST"])
@login_required
def categories():
    db = get_db()
    if request.method == "POST":
        if not can("can_edit"):
            abort(403)
        f = request.form
        action = f.get("action")
        try:
            if action == "add" and f.get("name", "").strip():
                db.execute("INSERT INTO categories (name, parent_id) VALUES (?,?)",
                           (f["name"].strip(), f.get("parent_id", type=int)))
                audit("Kategori eklendi", f["name"])
            elif action == "rename":
                db.execute("UPDATE categories SET name=? WHERE id=?",
                           (f.get("name", "").strip(), f.get("id", type=int)))
            elif action == "delete":
                cid = f.get("id", type=int)
                if db.execute("SELECT 1 FROM products WHERE category_id=?", (cid,)).fetchone() or \
                        db.execute("SELECT 1 FROM categories WHERE parent_id=?", (cid,)).fetchone():
                    flash("İçinde ürün veya alt kategori olan kategori silinemez.", "error")
                else:
                    db.execute("DELETE FROM categories WHERE id=?", (cid,))
            db.commit()
        except sqlite3.IntegrityError:
            db.rollback()
            flash("Bu isimde kategori zaten var.", "error")
        return redirect(url_for("categories"))
    counts = {r[0]: r[1] for r in db.execute(
        "SELECT category_id, COUNT(*) FROM products WHERE active=1 GROUP BY category_id")}
    return render_template("categories.html", cats=category_options(), counts=counts)


# =================================================================== Tedarikçiler
@app.route("/tedarikciler")
@permission_required("can_purchase", "can_in", "can_see_price", "can_report")
def suppliers():
    rows = get_db().execute(
        "SELECT s.*, (SELECT COUNT(*) FROM products WHERE default_supplier_id=s.id) AS n, "
        "(SELECT MAX(created_at) FROM movements WHERE supplier_id=s.id) AS last_buy "
        "FROM suppliers s ORDER BY s.active DESC, s.name").fetchall()
    return render_template("suppliers.html", rows=rows)


@app.route("/tedarikci/yeni", methods=["GET", "POST"])
@app.route("/tedarikci/<int:sid>/duzenle", methods=["GET", "POST"])
@permission_required("can_purchase")
def supplier_edit(sid=None):
    db = get_db()
    s = db.execute("SELECT * FROM suppliers WHERE id=?", (sid,)).fetchone() if sid else None
    if sid and not s:
        abort(404)
    fields = ["name", "contact", "phone", "email", "tax_office", "tax_no", "address", "website",
              "note"]
    data = dict(s) if s else {k: "" for k in fields} | {"active": 1}
    if request.method == "POST":
        data = {k: request.form.get(k, "").strip() for k in fields}
        data["active"] = 1 if request.form.get("active") or not sid else 0
        if not data["name"]:
            flash("Firma adı zorunlu.", "error")
        else:
            try:
                if sid:
                    db.execute(f"UPDATE suppliers SET {', '.join(k + '=?' for k in fields)}, "
                               f"active=? WHERE id=?", [data[k] for k in fields] +
                               [data["active"], sid])
                else:
                    sid = db.execute(f"INSERT INTO suppliers ({','.join(fields)}, created_at) "
                                     f"VALUES ({','.join('?' * len(fields))},?)",
                                     [data[k] for k in fields] + [now()]).lastrowid
                audit("Tedarikçi kaydedildi", data["name"])
                db.commit()
                flash("Tedarikçi kaydedildi.", "ok")
                return redirect(url_for("supplier_detail", sid=sid))
            except sqlite3.IntegrityError:
                db.rollback()
                flash("Bu isimde tedarikçi zaten var.", "error")
    return render_template("supplier_form.html", s=data, sid=sid)


@app.route("/tedarikci/<int:sid>")
@permission_required("can_purchase", "can_in", "can_see_price", "can_report")
def supplier_detail(sid):
    db = get_db()
    s = db.execute("SELECT * FROM suppliers WHERE id=?", (sid,)).fetchone() or abort(404)
    prods = db.execute("SELECT * FROM products WHERE default_supplier_id=? ORDER BY code",
                       (sid,)).fetchall()
    buys = db.execute(MOVE_SELECT + " WHERE m.supplier_id=? ORDER BY m.id DESC LIMIT 100",
                      (sid,)).fetchall()
    atts = [a for a in attachments_for("supplier", sid) if can_view_attachment(a)]
    return render_template("supplier_detail.html", s=s, prods=prods, buys=buys,
                           attachments=atts, ATTACH_KINDS=ATTACH_KINDS)


# =================================================================== Raporlar
@app.route("/raporlar")
@permission_required("can_report", "can_see_price")
def reports():
    return render_template("reports.html")


def report(title, headers, rows, note="", csv_name="rapor", right=()):
    if request.args.get("csv") == "1":
        return csv_response(csv_name, headers,
                            [[xl(c) if isinstance(c, float) else c for c in r] for r in rows])
    return render_template("report.html", title=title, headers=headers, rows=rows, note=note,
                           right=set(right), a=request.args)


@app.route("/rapor/tuketim")
@permission_required("can_report")
def report_consumption():
    a = request.args
    grp = a.get("grup", "proje")
    bas = a.get("bas") or datetime.now().strftime("%Y-%m-01")
    bit = a.get("bit") or datetime.now().strftime("%Y-%m-%d")
    price = can("can_see_price")
    val = ", SUM(m.qty * p.last_price) AS v, p.currency" if price else ""
    if grp == "urun":
        sql = (f"SELECT p.code, p.name, p.unit, SUM(m.qty) q, COUNT(*) n{val} FROM movements m "
               f"JOIN products p ON p.id=m.product_id WHERE m.type='CIKIS' AND m.created_at>=? "
               f"AND m.created_at<date(?,'+1 day') GROUP BY p.id ORDER BY q DESC")
        hdr = ["Stok Kodu", "Ürün", "Birim", "Çıkan Miktar", "Hareket"]
        mk = lambda r: [r["code"], r["name"], r["unit"], r["q"], r["n"]]
    else:
        col = {"proje": "m.project", "kullanici": "u.full_name", "departman": "u.department",
               "teslim": "m.receiver"}.get(grp, "m.project")
        sql = (f"SELECT {col} AS k, p.code, p.name, p.unit, SUM(m.qty) q{val} FROM movements m "
               f"JOIN products p ON p.id=m.product_id JOIN users u ON u.id=m.user_id "
               f"WHERE m.type='CIKIS' AND m.created_at>=? AND m.created_at<date(?,'+1 day') "
               f"GROUP BY {col}, p.id ORDER BY {col}, q DESC")
        hdr = [{"proje": "Proje / Makine", "kullanici": "Çıkışı Yapan", "departman": "Departman",
                "teslim": "Teslim Alan"}.get(grp, "Proje"), "Stok Kodu", "Ürün", "Birim", "Miktar"]
        mk = lambda r: [r["k"] or "(boş)", r["code"], r["name"], r["unit"], r["q"]]
    rows = []
    for r in get_db().execute(sql, (bas, bit)):
        line = mk(r)
        if price:
            line += [fmt_money(r["v"], r["currency"])]
        rows.append(line)
    if price:
        hdr = hdr + ["Tahmini Tutar (son fiyat)"]
    return report(f"Tüketim Raporu ({bas} – {bit})", hdr, rows, csv_name="tuketim",
                  note="filters_consumption", right=(3, 4, 5))


@app.route("/rapor/siparis")
@permission_required("can_report", "can_purchase")
def report_reorder():
    rows = get_db().execute(
        "SELECT p.*, s.name AS sname, s.phone, (SELECT COALESCE(SUM(ri.qty-ri.issued_qty),0) "
        "FROM request_items ri JOIN requests r ON r.id=ri.request_id WHERE ri.product_id=p.id "
        "AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI')) AS reserved "
        "FROM products p LEFT JOIN suppliers s ON s.id=p.default_supplier_id "
        "WHERE p.active=1 AND p.min_stock>0 AND p.quantity <= p.min_stock "
        "ORDER BY s.name, p.code").fetchall()
    out = []
    for r in rows:
        target = r["max_stock"] if r["max_stock"] > r["min_stock"] else r["min_stock"] * 2
        sug = max(target - r["quantity"] + r["reserved"], 0)
        line = [r["sname"] or "(tedarikçi yok)", r["code"], r["name"],
                f"{r['brand']} {r['model']}".strip(), r["unit"], r["quantity"], r["min_stock"],
                r["reserved"], sug]
        if can("can_see_price"):
            line += [fmt_money(r["last_price"], r["currency"]),
                     fmt_money(sug * r["last_price"], r["currency"])]
        out.append(line)
    hdr = ["Tedarikçi", "Kod", "Ürün", "Marka/Model", "Birim", "Mevcut", "Min.",
           "Açık Talep", "Önerilen Sipariş"]
    if can("can_see_price"):
        hdr += ["Son Fiyat", "Tutar"]
    return report("Sipariş Önerisi (minimum stok altı)", hdr, out, csv_name="siparis_onerisi",
                  right=(5, 6, 7, 8, 9, 10),
                  note="Önerilen = (Maks. stok veya 2×Min.) − Mevcut + Açık talepler")


@app.route("/rapor/deger")
@permission_required("can_see_price")
def report_value():
    grp = request.args.get("grup", "kategori")
    db = get_db()
    if grp == "lokasyon":
        paths = tree_paths("locations")
        data = {}
        for r in db.execute("SELECT s.location_id, p.currency, SUM(s.qty*p.last_price) v, "
                            "COUNT(*) n FROM stock s JOIN products p ON p.id=s.product_id "
                            "WHERE s.qty>0 GROUP BY s.location_id, p.currency"):
            data[(paths.get(r["location_id"], "?"), r["currency"])] = (r["n"], r["v"])
    else:
        paths = tree_paths("categories")
        data = {}
        for r in db.execute("SELECT category_id, currency, SUM(quantity*last_price) v, COUNT(*) n "
                            "FROM products WHERE active=1 AND quantity>0 "
                            "GROUP BY category_id, currency"):
            data[(paths.get(r["category_id"], "(kategorisiz)"), r["currency"])] = (r["n"], r["v"])
    rows = [[k[0], n, k[1], fmt_money(v, k[1])] for k, (n, v) in sorted(data.items())]
    totals = {}
    for (_, cur), (_, v) in data.items():
        totals[cur] = totals.get(cur, 0) + (v or 0)
    for cur, v in totals.items():
        rows.append(["TOPLAM", "", cur, fmt_money(v, cur)])
    return report("Stok Değeri (son alış fiyatı ile)",
                  ["Lokasyon" if grp == "lokasyon" else "Kategori", "Kalem", "Para Birimi",
                   "Değer"], rows, csv_name="stok_degeri", right=(1, 3), note="filters_value")


@app.route("/rapor/hareketsiz")
@permission_required("can_report")
def report_dead():
    days = request.args.get("gun", 180, type=int)
    price = can("can_see_price")
    rows = []
    for r in get_db().execute(
            "SELECT p.*, (SELECT MAX(created_at) FROM movements WHERE product_id=p.id "
            "AND type IN ('CIKIS','GIRIS')) AS last FROM products p WHERE p.active=1 "
            "AND p.quantity>0 AND COALESCE((SELECT MAX(created_at) FROM movements WHERE "
            "product_id=p.id AND type IN ('CIKIS','GIRIS')), p.created_at) < "
            "datetime('now','localtime', ?) ORDER BY last", (f"-{days} day",)):
        line = [r["code"], r["name"], r["quantity"], r["unit"], (r["last"] or "-")[:10]]
        if price:
            line.append(fmt_money(r["quantity"] * r["last_price"], r["currency"]))
        rows.append(line)
    hdr = ["Kod", "Ürün", "Miktar", "Birim", "Son Hareket"] + (["Bağlı Değer"] if price else [])
    return report(f"Hareketsiz Stok (son {days} gün giriş/çıkış yok)", hdr, rows,
                  csv_name="hareketsiz", right=(2, 5), note="filters_dead")


@app.route("/rapor/denetim")
@permission_required("is_admin")
def report_audit():
    rows = [[r["created_at"], r["full_name"] or "-", r["action"], r["detail"], r["ip"]]
            for r in get_db().execute(
                "SELECT a.*, u.full_name FROM audit a LEFT JOIN users u ON u.id=a.user_id "
                "ORDER BY a.id DESC LIMIT 1000")]
    return report("Denetim Kaydı (son 1000)", ["Zaman", "Kullanıcı", "İşlem", "Detay", "IP"],
                  rows, csv_name="denetim")


# ================================================================ QR Etiketler
def qr_svg(data, scale=3):
    try:
        import segno
    except ImportError:
        return Markup("<small>(QR için: pip install segno)</small>")
    buf = io.BytesIO()
    segno.make(data, error="m").save(buf, kind="svg", scale=scale, border=1, xmldecl=False,
                                    svgns=True)
    return Markup(buf.getvalue().decode())


@app.route("/etiket")
@login_required
def labels():
    db = get_db()
    base = request.host_url.rstrip("/")
    out = []
    pids = [int(x) for x in request.args.get("urun", "").split(",") if x.isdigit()]
    lids = [int(x) for x in request.args.get("lokasyon", "").split(",") if x.isdigit()]
    if request.args.get("alt") == "1":  # lokasyonun tüm alt lokasyonları
        lids = [i for lid in lids for i in subtree_ids("locations", lid)]
    paths = tree_paths("locations")
    for pid in pids[:300]:
        p = db.execute("SELECT * FROM products WHERE id=?", (pid,)).fetchone()
        if p:
            out.append({"qr": qr_svg(f"{base}/urun/{p['id']}"), "title": p["code"],
                        "sub": p["name"], "extra": f"{p['brand']} {p['model']}".strip()})
    for lid in lids[:300]:
        loc = db.execute("SELECT * FROM locations WHERE id=?", (lid,)).fetchone()
        if loc:
            out.append({"qr": qr_svg(f"{base}/lokasyon/{loc['id']}"), "title": loc["code"],
                        "sub": loc["name"], "extra": paths.get(lid, "")})
    return render_template("labels.html", labels=out)


# =================================================================== İçe aktarma
@app.route("/urun/ice-aktar", methods=["GET", "POST"])
@permission_required("can_edit")
def product_import():
    """CSV: kod;ad;kategori;birim;lokasyon_kodu;min_stok;miktar;marka;model;tedarikci;fiyat;para"""
    if request.method == "POST":
        f = request.files.get("file")
        if not f or not f.filename:
            flash("Dosya seçin.", "error")
            return redirect(request.url)
        text = f.read().decode("utf-8-sig", errors="replace")
        delim = ";" if text.count(";") >= text.count(",") else ","
        db = get_db()
        added, skipped, errors = 0, 0, []
        db.execute("BEGIN IMMEDIATE")
        for i, row in enumerate(csv.reader(io.StringIO(text), delimiter=delim), 1):
            if not row or not row[0].strip():
                continue
            if i == 1 and row[0].strip().lower() in ("kod", "stok kodu", "code"):
                continue
            row = [c.strip() for c in row] + [""] * 12
            code, name, cat, unit, loc, mn, qty, brand, model, sup, price, cur = row[:12]
            mn_v, qty_v, pr_v = parse_num(mn, 0), parse_num(qty, 0), parse_num(price, 0)
            if not name or mn_v is None or qty_v is None or qty_v < 0 or pr_v is None:
                errors.append(f"Satır {i}: hatalı veri")
                continue
            if db.execute("SELECT 1 FROM products WHERE code=?", (code,)).fetchone():
                skipped += 1
                continue
            cat_id = None
            if cat:  # "Elektrik/Kontaktör" -> alt kategoriler otomatik
                parent = None
                for part in [c.strip() for c in cat.split("/") if c.strip()]:
                    r = db.execute("SELECT id FROM categories WHERE name=? AND parent_id IS ?",
                                   (part, parent)).fetchone()
                    parent = r["id"] if r else db.execute(
                        "INSERT INTO categories (name, parent_id) VALUES (?,?)",
                        (part, parent)).lastrowid
                cat_id = parent
            loc_id = None
            if loc:
                r = db.execute("SELECT id FROM locations WHERE code=?", (loc,)).fetchone()
                loc_id = r["id"] if r else db.execute(
                    "INSERT INTO locations (code, name, type, parent_id) VALUES (?,?,'Raf',"
                    "(SELECT id FROM locations WHERE parent_id IS NULL ORDER BY id LIMIT 1))",
                    (loc.upper(), loc)).lastrowid
            sup_id = None
            if sup:
                r = db.execute("SELECT id FROM suppliers WHERE name=?", (sup,)).fetchone()
                sup_id = r["id"] if r else db.execute(
                    "INSERT INTO suppliers (name, created_at) VALUES (?,?)",
                    (sup, now())).lastrowid
            if qty_v and not loc_id:
                loc_id = db.execute("SELECT id FROM locations WHERE parent_id IS NULL "
                                    "ORDER BY id LIMIT 1").fetchone()["id"]
            pid = db.execute(
                "INSERT INTO products (code, name, category_id, unit, default_location_id, "
                "min_stock, brand, model, default_supplier_id, last_price, currency, created_by, "
                "created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (code, name, cat_id, unit or "adet", loc_id, mn_v, brand, model, sup_id, pr_v,
                 cur.upper() if cur.upper() in CURRENCIES else "TRY", g.user["id"], now(),
                 now())).lastrowid
            if qty_v:
                apply_move(db, pid, "SAYIM", qty_v, None, loc_id, note="Toplu içe aktarma")
            added += 1
        audit("Toplu ürün içe aktarma", f"{added} eklendi, {skipped} atlandı")
        db.commit()
        flash(f"{added} ürün eklendi, {skipped} mevcut kod atlandı.", "ok")
        for e in errors[:10]:
            flash(e, "error")
        return redirect(url_for("products"))
    return render_template("import.html")


# =================================================================== Kullanıcılar
@app.route("/kullanicilar")
@permission_required("is_admin")
def users():
    rows = get_db().execute("SELECT * FROM users ORDER BY active DESC, full_name").fetchall()
    return render_template("users.html", rows=rows)


@app.route("/kullanici/yeni", methods=["GET", "POST"])
@app.route("/kullanici/<int:uid>", methods=["GET", "POST"])
@permission_required("is_admin")
def user_edit(uid=None):
    db = get_db()
    u = db.execute("SELECT * FROM users WHERE id=?", (uid,)).fetchone() if uid else None
    if uid and not u:
        abort(404)
    if request.method == "POST":
        f = request.form
        username = f.get("username", "").strip()
        info = {k: f.get(k, "").strip() for k in ("full_name", "department", "role", "phone")}
        password = f.get("password", "")
        perms = {k: 1 if f.get(k) else 0 for k in PERM_KEYS}
        active = 1 if f.get("active") else 0
        errors = []
        if not username or not info["full_name"]:
            errors.append("Kullanıcı adı ve ad soyad zorunlu.")
        if (not uid or password) and len(password) < 6:
            errors.append("Şifre en az 6 karakter olmalı.")
        if uid == g.user["id"] and (not perms["is_admin"] or not active):
            errors.append("Kendi yönetici yetkinizi veya hesabınızı kapatamazsınız.")
        if not errors:
            try:
                if uid:
                    db.execute(
                        "UPDATE users SET username=?, full_name=?, department=?, role=?, phone=?,"
                        " active=?, " + ", ".join(f"{k}=?" for k in PERM_KEYS) + " WHERE id=?",
                        (username, *info.values(), active, *perms.values(), uid))
                    if password:
                        db.execute("UPDATE users SET password_hash=?, must_change_pw=1 WHERE id=?",
                                   (generate_password_hash(password), uid))
                else:
                    db.execute(
                        "INSERT INTO users (username, full_name, department, role, phone, "
                        "password_hash, must_change_pw, active, " + ", ".join(PERM_KEYS) +
                        ", created_at) VALUES (?,?,?,?,?,?,1,?," +
                        ",".join("?" * len(PERM_KEYS)) + ",?)",
                        (username, *info.values(), generate_password_hash(password), active,
                         *perms.values(), now()))
                audit("Kullanıcı kaydedildi", f"{username} ({info['role']}): " +
                      ", ".join(k for k, v in perms.items() if v))
                db.commit()
                flash("Kullanıcı kaydedildi.", "ok")
                return redirect(url_for("users"))
            except sqlite3.IntegrityError:
                db.rollback()
                errors.append("Bu kullanıcı adı zaten var.")
        for e in errors:
            flash(e, "error")
        u = dict(username=username, active=active, id=uid, **info, **perms)
    depts = [r[0] for r in db.execute(
        "SELECT DISTINCT department FROM users WHERE department<>'' ORDER BY 1")]
    return render_template("user_form.html", u=u, ROLES=ROLES, depts=depts)


# =================================================================== Hatalar
@app.errorhandler(403)
def forbidden(_e):
    return render_template("error.html", msg="Bu işlem için yetkiniz yok."), 403


@app.errorhandler(404)
def not_found(_e):
    return render_template("error.html", msg="Sayfa veya kayıt bulunamadı."), 404


@app.errorhandler(400)
def bad_request(e):
    return render_template("error.html", msg=e.description), 400


@app.errorhandler(413)
def too_large(_e):
    return render_template("error.html", msg="Dosya çok büyük (en fazla 40 MB)."), 413


init_db()

if __name__ == "__main__":
    host = os.environ.get("STOK_HOST", "0.0.0.0")
    port = int(os.environ.get("STOK_PORT", "8000"))
    try:
        from waitress import serve
        print(f">> {COMPANY} Stok Takip çalışıyor: http://{host}:{port}  (durdurmak: Ctrl+C)")
        serve(app, host=host, port=port, threads=12)
    except ImportError:
        print(">> waitress yok, geliştirme sunucusu kullanılıyor")
        app.run(host=host, port=port)

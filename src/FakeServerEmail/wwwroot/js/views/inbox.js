import { api, encode } from "../lib/api.js";
import { debounce, displayAddress, fmtDate, fmtShort, fmtSize, isSeen, plural } from "../lib/format.js";

const TABS = [
  { id: "html", label: "HTML" },
  { id: "text", label: "Texto" },
  { id: "headers", label: "Cabeçalhos" },
  { id: "source", label: "Código-fonte" },
  { id: "attachments", label: "Anexos" },
  { id: "delivery", label: "Entrega" },
];

export default () => ({
  tabs: TABS,
  folders: [],
  folder: "INBOX",
  messages: [],
  search: "",
  selectedId: null,
  detail: null,
  tab: "html",
  preferredTab: "html",
  source: "",
  refreshing: false,

  init() {
    this.reload();
    const changed = debounce(() => this.reload(), 150);
    window.addEventListener("fse-messages", changed);
    window.addEventListener("fse-connected", changed);
  },

  fmtSize,
  fmtDate,
  fmtShort,
  isSeen,
  who: (m) => displayAddress(m.from || m.envelopeFrom),
  recipients: (m) => (m.envelopeTo?.length ? m.envelopeTo.join(", ") : m.to) || "",
  showFolderTag(m) {
    return this.folder === "*" || m.folder !== "INBOX";
  },

  get filtered() {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.messages;
    return this.messages.filter((m) => [m.subject, m.from, m.to, m.cc, m.envelopeFrom, ...(m.envelopeTo || [])].join(" ").toLowerCase().includes(q));
  },

  get smtpAddress() {
    return "localhost:" + (this.$store.app.port("SMTP") ?? "?");
  },

  async reload() {
    await Promise.all([this.loadFolders(), this.loadMessages()]);
    if (this.detail) {
      const fresh = this.messages.find((m) => m.id === this.selectedId);
      if (fresh) this.detail.summary = fresh;
    }
  },

  async loadFolders() {
    try {
      this.folders = await api("/api/folders");
    } catch {
      return;
    }
    if (this.folder !== "*" && !this.folders.some((f) => f.name === this.folder)) this.folder = "INBOX";
  },

  folderLabel(f) {
    const name = f.name === "INBOX" ? "Caixa de entrada" : f.name;
    return `${name} · ${f.total}${f.unread ? ` (${f.unread} não ${f.unread === 1 ? "lida" : "lidas"})` : ""}`;
  },

  async loadMessages() {
    try {
      this.messages = await api("/api/messages?folder=" + encode(this.folder));
    } catch (err) {
      this.$store.app.notify(err.message, true);
      return;
    }
    if (this.selectedId && !this.messages.some((m) => m.id === this.selectedId)) this.close();
  },

  changeFolder() {
    this.close();
    this.loadMessages();
  },

  async refresh() {
    if (this.refreshing) return;
    this.refreshing = true;
    try {
      await Promise.all([this.loadFolders(), this.loadMessages(), this.$store.app.loadStatus(), new Promise((r) => setTimeout(r, 400))]);
      if (this.selectedId) await this.select(this.selectedId);
      this.$store.app.notify("Caixa de entrada atualizada: " + plural(this.messages.length, "mensagem", "mensagens"));
    } catch (err) {
      this.$store.app.notify(err.message, true);
    } finally {
      this.refreshing = false;
    }
  },

  async select(id) {
    this.selectedId = id;
    let detail;
    try {
      detail = await api("/api/messages/" + id);
    } catch (err) {
      this.$store.app.notify(err.message, true);
      return;
    }
    if (this.selectedId !== id) return;
    this.detail = detail;
    this.source = "";
    this.showTab(this.available(this.preferredTab) ? this.preferredTab : detail.hasHtml ? "html" : "text", false);
    if (!isSeen(detail.summary)) api(`/api/messages/${id}/seen`, { method: "POST", json: { seen: true } }).catch(() => { });
  },

  close() {
    this.selectedId = null;
    this.detail = null;
  },

  available(tab) {
    if (!this.detail) return false;
    if (tab === "html") return this.detail.hasHtml;
    if (tab === "text") return this.detail.text != null;
    return true;
  },

  tabLabel(tab) {
    const count = this.detail?.attachments.length ?? 0;
    return tab.id === "attachments" && count ? `Anexos (${count})` : tab.label;
  },

  async showTab(tab, remember = true) {
    if (remember) this.preferredTab = tab;
    this.tab = tab;
    if (tab === "source" && !this.source && this.detail) {
      this.source = "Carregando...";
      try {
        this.source = await api(`/api/messages/${this.detail.summary.id}/raw`);
      } catch (err) {
        this.source = err.message;
      }
    }
  },

  get htmlSrc() {
    return this.detail?.hasHtml ? `/api/messages/${this.detail.summary.id}/html` : "about:blank";
  },

  get fieldRows() {
    const d = this.detail;
    if (!d) return [];
    return [
      ["De", d.from],
      ["Para", d.to],
      ["Cc", d.cc],
      ["Cco", d.bcc],
      ["Responder a", d.replyTo],
      ["Data", d.date ? fmtDate(d.date) : ""],
      ["Recebida", fmtDate(d.summary.receivedAt)],
    ].filter((r) => r[1]);
  },

  get deliveryRows() {
    const d = this.detail;
    if (!d) return [];
    const s = d.summary;
    return [
      ["Aviso", d.parseError],
      ["Origem", s.source],
      ["MAIL FROM", s.envelopeFrom || "(vazio)"],
      ["RCPT TO", (s.envelopeTo || []).join(", ")],
      ["Cliente", s.clientAddress],
      ["Autenticado como", s.authenticatedUser || "(sem autenticação)"],
      ["Pasta", s.folder],
      ["UID IMAP", s.uid],
      ["Flags", s.flags.join(" ") || "(nenhuma)"],
      ["Tamanho", fmtSize(s.size)],
      ["Message-ID", d.messageId],
      ["ID interno", s.id],
    ].filter((r) => r[1] !== undefined && r[1] !== null && r[1] !== "");
  },

  partUrl(a, download = false) {
    return `/api/messages/${this.detail.summary.id}/parts/${a.index}${download ? "?download=true" : ""}`;
  },

  attachmentInfo(a) {
    return `${a.contentType} · ${fmtSize(a.size)}${a.inline ? " · inline" : ""}${a.contentId ? " · cid:" + a.contentId : ""}`;
  },

  async remove() {
    if (!this.selectedId) return;
    await api("/api/messages/" + this.selectedId, { method: "DELETE" });
    this.close();
  },

  async markUnread() {
    if (!this.selectedId) return;
    await api(`/api/messages/${this.selectedId}/seen`, { method: "POST", json: { seen: false } });
    this.$store.app.notify("Marcada como não lida");
  },

  readAll() {
    return api("/api/messages/seen-all?folder=" + encode(this.folder), { method: "POST" });
  },

  async deleteAll() {
    const where = this.folder === "*" ? "de todas as pastas" : "desta pasta";
    if (!confirm(`Apagar todas as mensagens ${where}?`)) return;
    await api("/api/messages?folder=" + encode(this.folder), { method: "DELETE" });
    this.close();
  },

  openSend() {
    window.dispatchEvent(new CustomEvent("fse-open-send"));
  },

  onKey(e) {
    if (this.$store.app.view !== "inbox" || e.target.matches("input, textarea, select") || document.querySelector("dialog[open]")) return;
    if (e.key === "Delete" && this.selectedId) this.remove();
    if (!["ArrowDown", "ArrowUp", "j", "k"].includes(e.key)) return;
    const list = this.filtered;
    if (list.length === 0) return;
    const i = list.findIndex((m) => m.id === this.selectedId);
    const next = e.key === "ArrowDown" || e.key === "j" ? Math.min(list.length - 1, i + 1) : Math.max(0, i - 1);
    this.select(list[next].id);
    this.$nextTick(() => this.$root.querySelector(`[data-id="${list[next].id}"]`)?.scrollIntoView({ block: "nearest" }));
    e.preventDefault();
  },
});

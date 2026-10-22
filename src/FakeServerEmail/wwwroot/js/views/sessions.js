import { api } from "../lib/api.js";
import { debounce, fmtDate, fmtShort, fmtTime } from "../lib/format.js";

const PREFIX = { C: "C: ", S: "S: " };
const CLASS = { C: "c", S: "s" };

export default () => ({
  sessions: [],
  protocol: "",
  selectedId: null,
  current: null,
  lines: [],

  init() {
    const changed = debounce(() => this.active() && this.load(), 250);
    window.addEventListener("fse-sessions", changed);
    window.addEventListener("fse-connected", changed);
    this.$watch("$store.app.view", () => this.active() && this.load());
    if (this.active()) this.load();
  },

  fmtShort,

  active() {
    return this.$store.app.view === "sessions";
  },

  get filtered() {
    return this.sessions.filter((s) => !this.protocol || s.protocol === this.protocol);
  },

  async load() {
    try {
      this.sessions = await api("/api/sessions");
    } catch {
      return;
    }
    if (this.selectedId) this.open(this.selectedId, false);
  },

  async open(id, scroll = true) {
    this.selectedId = id;
    let data;
    try {
      data = await api("/api/sessions/" + id);
    } catch {
      return;
    }
    const body = this.$refs.body;
    const atBottom = body && body.scrollTop + body.clientHeight >= body.scrollHeight - 30;
    this.current = data.session;
    this.lines = data.lines;
    this.$nextTick(() => {
      if (this.$refs.body && (scroll || atBottom)) this.$refs.body.scrollTop = this.$refs.body.scrollHeight;
    });
  },

  async clear() {
    await api("/api/sessions", { method: "DELETE" });
    this.selectedId = null;
    this.current = null;
    this.lines = [];
  },

  close() {
    this.current = null;
  },

  tags(s) {
    const tags = [{ text: `${s.protocol} :${s.port}`, cls: "" }];
    if (s.secure) tags.push({ text: "TLS", cls: "ok" });
    if (s.user) tags.push({ text: `🔑 ${s.user}${s.authMechanism ? " · " + s.authMechanism : ""}`, cls: "ok" });
    if (s.messagesReceived) tags.push({ text: `${s.messagesReceived} mensagem(ns)`, cls: "" });
    if (s.failures || s.error) tags.push({ text: s.failures ? `${s.failures} falha(s)` : "erro", cls: "bad" });
    if (!s.endedAt) tags.push({ text: "aberta", cls: "warn" });
    return tags;
  },

  get title() {
    return this.current ? `${this.current.protocol} · ${this.current.client}` : "";
  },

  get meta() {
    const s = this.current;
    if (!s) return "";
    return `Porta ${s.port} · início ${fmtDate(s.startedAt)}${s.endedAt ? " · fim " + fmtDate(s.endedAt) : " · conexão aberta"}${s.user ? " · usuário " + s.user : ""}`;
  },

  lineTime: (l) => fmtTime(l.time) + " ",
  lineClass: (l) => CLASS[l.direction] ?? "i",
  lineText: (l) => (PREFIX[l.direction] ?? "   ") + l.text,
});

import { api } from "../lib/api.js";
import { debounce, fmtDate, fmtShort, fmtTime, plural } from "../lib/format.js";

const PREFIX = { C: "C: ", S: "S: " };
const CLASS = { C: "c", S: "s" };

export default () => ({
  loadSeq: 0,
  openSeq: 0,
  sessions: [],
  protocol: "",
  selectedId: null,
  current: null,
  lines: [],

  init() {
    const quiet = () => this.active() && this.load().catch(() => { });
    const changed = debounce(quiet, 250);
    window.addEventListener("fse-sessions", changed);
    window.addEventListener("fse-connected", changed);
    this.$watch("$store.app.view", quiet);
    quiet();
  },

  fmtShort,

  active() {
    return this.$store.app.view === "sessions";
  },

  get filtered() {
    return this.sessions.filter((s) => !this.protocol || s.protocol === this.protocol);
  },

  async load() {
    const seq = ++this.loadSeq;
    const sessions = await api("/api/sessions");
    if (seq !== this.loadSeq) return;
    this.sessions = sessions;
    if (this.selectedId) await this.open(this.selectedId, false);
  },

  refresh() {
    return this.$store.app.run(() => this.load());
  },

  async open(id, fromUser = true) {
    this.selectedId = id;
    const seq = ++this.openSeq;
    let data;
    try {
      data = await api("/api/sessions/" + id);
    } catch (err) {
      if (fromUser && seq === this.openSeq) this.$store.app.notify(err.message, true);
      return;
    }
    if (seq !== this.openSeq) return;
    const body = this.$refs.body;
    const atBottom = body && body.scrollTop + body.clientHeight >= body.scrollHeight - 30;
    this.current = data.session;
    this.lines = data.lines;
    this.$nextTick(() => {
      if (this.$refs.body && (fromUser || atBottom)) this.$refs.body.scrollTop = this.$refs.body.scrollHeight;
    });
  },

  clear() {
    return this.$store.app.run(async () => {
      await api("/api/sessions", { method: "DELETE" });
      this.close();
    });
  },

  close() {
    this.openSeq++;
    this.selectedId = null;
    this.current = null;
    this.lines = [];
  },

  tags(s) {
    const tags = [{ text: `${s.protocol} :${s.port}`, cls: "" }];
    if (s.secure) tags.push({ text: "TLS", cls: "ok" });
    if (s.user) tags.push({ text: `🔑 ${s.user}${s.authMechanism ? " · " + s.authMechanism : ""}`, cls: "ok" });
    if (s.messagesReceived) tags.push({ text: plural(s.messagesReceived, "mensagem", "mensagens"), cls: "" });
    if (s.failures || s.error) tags.push({ text: s.failures ? plural(s.failures, "falha", "falhas") : "erro", cls: "bad" });
    if (!s.endedAt) tags.push({ text: "aberta", cls: "live" });
    return tags;
  },

  lineCount: (s) => plural(s.lineCount, "linha", "linhas"),

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

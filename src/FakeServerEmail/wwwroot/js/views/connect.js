import { api } from "../lib/api.js";
import { fmtDate } from "../lib/format.js";
import { buildSnippets, MCP_TOOLS } from "./connect-snippets.js";

export default () => ({
  active: {},
  tools: MCP_TOOLS,

  fmtDate,

  get status() {
    return this.$store.app.status;
  },

  get ctx() {
    const st = this.status;
    if (!st) return null;
    const port = (name) => this.$store.app.port(name);
    const show = (name) => port(name) ?? "—";
    return {
      smtp: show("SMTP"),
      pop: show("POP3"),
      imap: show("IMAP"),
      smtps: show("SMTPS"),
      pops: show("POP3S"),
      imaps: show("IMAPS"),
      smtpNumber: port("SMTP"),
      popNumber: port("POP3"),
      imapNumber: port("IMAP"),
      user: st.authentication.exampleUser,
      pass: st.authentication.examplePassword,
      acceptAny: st.authentication.acceptAnyPassword,
      tokenUrl: st.oauth.httpToken,
      authority: st.oauth.microsoftAuthority,
      msToken: st.oauth.microsoftToken,
      mcpHttp: st.mcp.http,
      exe: st.mcp.stdioCommand || "C:\\caminho\\FakeServerEmail.exe",
      network: st.allowNetworkAccess,
    };
  },

  get snippets() {
    return this.ctx ? buildSnippets(this.ctx) : {};
  },

  langs(group) {
    return Object.keys(this.snippets[group] || {});
  },

  activeLang(group) {
    const langs = this.langs(group);
    return langs.includes(this.active[group]) ? this.active[group] : langs[0];
  },

  setLang(group, lang) {
    this.active = { ...this.active, [group]: lang };
  },

  code(group) {
    return this.snippets[group]?.[this.activeLang(group)] ?? "";
  },

  get services() {
    return this.status?.services ?? [];
  },

  security(s) {
    if (s.tls === "implicit") return "TLS direto (SSL)";
    if (s.tls === "none") return "Sem TLS (STARTTLS desligado)";
    return s.protocol === "POP3" ? "Sem TLS ou STLS" : "Sem TLS ou STARTTLS";
  },

  async setTrust(trust) {
    try {
      await api("/api/certificate/" + (trust ? "trust" : "untrust"), { method: "POST" });
      this.$store.app.notify(trust ? "Certificado marcado como confiável" : "Confiança removida");
    } catch (err) {
      this.$store.app.notify(err.message, true);
    }
    this.$store.app.loadStatus();
  },
});

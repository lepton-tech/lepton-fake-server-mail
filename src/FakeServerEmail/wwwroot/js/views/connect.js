import { buildSnippets, MCP_TOOLS } from "./connect-snippets.js";

const built = new WeakMap();

export default () => ({
  active: {},
  tools: MCP_TOOLS,

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
    const st = this.status;
    if (!st) return {};
    if (!built.has(st)) built.set(st, buildSnippets(this.ctx));
    return built.get(st);
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
    return (this.status?.services ?? []).filter((s) => s.configuredPort > 0);
  },

  get offServices() {
    return (this.status?.services ?? []).filter((s) => s.configuredPort <= 0).map((s) => s.name);
  },

  get readingOff() {
    return !this.$store.app.port("POP3") && !this.$store.app.port("IMAP");
  },

  openPorts() {
    this.$store.app.setView("settings", "portas");
  },

  security(s) {
    if (s.tls === "implicit") return "TLS direto (SSL)";
    if (s.tls === "none") return "Sem TLS (STARTTLS desligado)";
    return s.protocol === "POP3" ? "Sem TLS ou STLS" : "Sem TLS ou STARTTLS";
  },

  openCertificate() {
    this.$store.app.setView("settings", "certificado");
  },
});

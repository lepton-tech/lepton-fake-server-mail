import { api } from "../lib/api.js";
import { fmtDate } from "../lib/format.js";

const PORTS = [
  { key: "smtpPort", label: "SMTP", standard: 25 },
  { key: "smtpSubmissionPort", label: "SMTP submissão", standard: 587 },
  { key: "smtpsPort", label: "SMTPS (TLS)", standard: 465 },
  { key: "pop3Port", label: "POP3", standard: 110 },
  { key: "pop3sPort", label: "POP3S (TLS)", standard: 995 },
  { key: "imapPort", label: "IMAP", standard: 143 },
  { key: "imapsPort", label: "IMAPS (TLS)", standard: 993 },
  { key: "httpPort", label: "Interface web (HTTP)", standard: 5080, required: true },
  { key: "httpsPort", label: "OAuth/Web (HTTPS)", standard: 5443 },
];

const MECHANISMS = [
  ["PLAIN", "Usuário e senha num passo só. O mais comum."],
  ["LOGIN", "Usuário e senha em dois passos. Legado, ainda usado pelo Outlook e por clientes antigos."],
  ["XOAUTH2", "Token OAuth no formato do Google e da Microsoft."],
  ["OAUTHBEARER", "Token OAuth no formato padrão (RFC 7628)."],
];

let nextRowId = 1;

const row = (username = "", password = "") => ({ id: nextRowId++, username, password });

export default () => ({
  ports: PORTS,
  mechanisms: MECHANISMS,
  model: null,
  off: {},
  userRows: [],
  message: "",

  fmtDate,

  init() {
    this.$watch("$store.app.view", (view) => view === "settings" && this.load());
    if (this.$store.app.view === "settings") this.load();
  },

  get info() {
    const st = this.$store.app.status;
    return st ? `Versão ${st.version} · iniciado em ${fmtDate(st.startedAt)}` : "";
  },

  get dataDirectory() {
    return this.$store.app.status?.dataDirectory ?? "";
  },

  get certificate() {
    return this.$store.app.status?.certificate ?? null;
  },

  toggle(p, on) {
    this.off[p.key] = !on;
    if (on && !(parseInt(this.model[p.key], 10) > 0)) this.model[p.key] = p.standard;
  },

  get passwordMode() {
    return this.model?.acceptAnyPassword ? "any" : "users";
  },

  setPasswordMode(mode) {
    this.model.acceptAnyPassword = mode === "any";
    if (mode === "users" && this.userRows.length === 0) this.addUser();
  },

  addUser() {
    this.userRows.push(row());
    this.$nextTick(() => [...this.$root.querySelectorAll('.users input[aria-label="Usuário"]')].pop()?.focus());
  },

  removeUser(id) {
    this.userRows = this.userRows.filter((u) => u.id !== id);
  },

  async load() {
    const s = await api("/api/settings");
    this.userRows = (s.users || []).map((u) => row(u.username, u.password));
    this.off = Object.fromEntries(PORTS.map((p) => [p.key, !p.required && !(s[p.key] > 0)]));
    this.model = s;
    this.message = "";
    const anchor = this.$store.app.anchor;
    if (anchor) {
      this.$store.app.anchor = null;
      this.$nextTick(() => document.getElementById(anchor)?.scrollIntoView({ block: "start" }));
    }
  },

  async save() {
    const users = this.userRows.map((u) => ({ username: u.username.trim(), password: u.password })).filter((u) => u.username);
    if (!this.model.acceptAnyPassword && users.length === 0) {
      this.$store.app.notify("Cadastre pelo menos um usuário ou volte para \"Aceitar qualquer usuário e senha\".", true);
      return;
    }
    const settings = { ...this.model, users };
    for (const p of PORTS) {
      const port = parseInt(settings[p.key] || "0", 10);
      if (this.off[p.key]) {
        settings[p.key] = 0;
      } else if (port >= 1 && port <= 65535) {
        settings[p.key] = port;
      } else {
        this.$store.app.notify(`Informe uma porta de 1 a 65535 para ${p.label}${p.required ? "" : " ou desmarque o serviço"}.`, true);
        return;
      }
    }
    settings.tokenLifetimeSeconds = parseInt(settings.tokenLifetimeSeconds || "0", 10);
    settings.maxMessageSizeMb = parseInt(settings.maxMessageSizeMb || "0", 10);
    try {
      const result = await api("/api/settings", { method: "PUT", json: settings });
      let msg = "Configurações salvas.";
      if (result.servicesRestarted) msg += " Serviços reiniciados com as novas portas.";
      if (result.restartRequired) msg += " Feche e abra o aplicativo para aplicar as portas da interface web.";
      this.message = msg;
      this.$store.app.notify("Configurações salvas");
      this.$store.app.loadStatus();
    } catch (err) {
      this.$store.app.notify(err.message, true);
    }
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

  openFolder() {
    return api("/api/open-data-folder", { method: "POST" });
  },

  async clearData() {
    if (!confirm("Apagar todos os e-mails, pastas criadas, tokens OAuth e o log de conexões? As configurações serão mantidas.")) return;
    try {
      const r = await api("/api/data/clear", { method: "POST" });
      this.$store.app.notify(`Dados limpos: ${r.messagesDeleted} e-mails e ${r.tokensDeleted} tokens apagados`);
    } catch (err) {
      this.$store.app.notify(err.message, true);
    }
  },

  async removeData() {
    const trusted = this.$store.app.status?.certificate?.trusted;
    const text = `Remover os dados deste computador?\n\nSerá apagado tudo o que o Fake Server Email gravou em:\n${this.dataDirectory}\n\n(e-mails, tokens, configurações, chave do OAuth e certificado)` +
      (trusted ? "\n\nO certificado também sai da lista de confiáveis do Windows; o Windows vai pedir uma confirmação." : "") +
      "\n\nO programa será fechado. Não dá para desfazer.";
    if (!confirm(text)) return;
    try {
      const r = await api("/api/data/remove", { method: "POST" });
      this.$store.app.stop("Dados removidos", "Tudo o que o programa gravou foi apagado e o servidor foi fechado.", {
        path: r.dataDirectory,
        warning: r.warning,
        hint: "Se não for mais usar, basta apagar o FakeServerEmail.exe. Se abrir de novo, ele começa do zero.",
      });
    } catch (err) {
      this.$store.app.notify(err.message, true);
    }
  },

  async shutdown() {
    if (!confirm("Encerrar o Fake Server Email? Os serviços SMTP, POP3, IMAP e OAuth vão parar.")) return;
    await api("/api/shutdown", { method: "POST" });
    this.$store.app.stop("Servidor encerrado", "Abra o FakeServerEmail.exe novamente para voltar a usar.");
  },
});

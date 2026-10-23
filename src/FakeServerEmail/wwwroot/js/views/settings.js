import { api } from "../lib/api.js";
import { fmtDate } from "../lib/format.js";

const PORT_GROUPS = [
  {
    title: "Enviar e-mails",
    hint: "É por aqui que o aplicativo testado envia. As três opções funcionam igual neste servidor: ligue a que o aplicativo pedir. Na dúvida, deixe o SMTP e o SMTP submissão ligados. O SMTPS é o envio com criptografia desde o início.",
    ports: [
      { key: "smtpPort", label: "SMTP", standard: 25 },
      { key: "smtpSubmissionPort", label: "SMTP submissão", standard: 587 },
      { key: "smtpsPort", label: "SMTPS (com TLS)", standard: 465 },
    ],
  },
  {
    title: "Ler e-mails",
    hint: "Só é preciso se o aplicativo testado também busca e-mails numa caixa de entrada. POP3S e IMAPS são as versões com criptografia desde o início.",
    ports: [
      { key: "pop3Port", label: "POP3", standard: 110 },
      { key: "pop3sPort", label: "POP3S (com TLS)", standard: 995 },
      { key: "imapPort", label: "IMAP", standard: 143 },
      { key: "imapsPort", label: "IMAPS (com TLS)", standard: 993 },
    ],
  },
  {
    title: "Interface web e OAuth",
    hint: "O HTTP é esta tela e fica sempre ligado. O HTTPS só é usado no login OAuth por bibliotecas que exigem conexão segura, como a MSAL da Microsoft.",
    ports: [
      { key: "httpPort", label: "Interface web (HTTP)", standard: 5080, required: true },
      { key: "httpsPort", label: "OAuth/Web (HTTPS)", standard: 5443 },
    ],
  },
];

const PORTS = PORT_GROUPS.flatMap((g) => g.ports);

const MECHANISMS = [
  ["PLAIN", "Usuário e senha num passo só. O mais comum."],
  ["LOGIN", "Usuário e senha em dois passos. Legado, ainda usado pelo Outlook e por clientes antigos."],
  ["XOAUTH2", "Token OAuth no formato do Google e da Microsoft."],
  ["OAUTHBEARER", "Token OAuth no formato padrão (RFC 7628)."],
];

let nextRowId = 1;

const row = (username = "", password = "") => ({ id: nextRowId++, username, password });

export default () => ({
  portGroups: PORT_GROUPS,
  mechanisms: MECHANISMS,
  model: null,
  off: {},
  userRows: [],
  message: "",
  saved: "",
  saving: false,
  externalChange: false,
  trustBusy: false,

  fmtDate,

  init() {
    this.$watch("$store.app.view", () => this.active() && this.enter());
    window.addEventListener("fse-settings", () => this.changedElsewhere());
    if (this.active()) this.enter();
  },

  active() {
    return this.$store.app.view === "settings";
  },

  snapshot() {
    return JSON.stringify([this.model, this.userRows.map((u) => [u.username, u.password]), this.off]);
  },

  get dirty() {
    return this.model !== null && this.snapshot() !== this.saved;
  },

  async enter() {
    if (!this.dirty) await this.$store.app.run(() => this.load());
    const anchor = this.$store.app.anchor;
    if (!anchor) return;
    this.$store.app.anchor = null;
    this.$nextTick(() => document.getElementById(anchor)?.scrollIntoView({ block: "start" }));
  },

  changedElsewhere() {
    if (this.saving || this.model === null) return;
    if (this.dirty) this.externalChange = true;
    else if (this.active()) this.$store.app.run(() => this.load(true));
  },

  discard() {
    return this.$store.app.run(() => this.load());
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

  suggestion(p) {
    const port = parseInt(this.model?.[p.key], 10);
    return port > 0 ? port : p.standard;
  },

  toggle(p, on) {
    this.off[p.key] = !on;
    if (on) this.model[p.key] = this.suggestion(p);
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
    this.$nextTick(() => [...this.$root.querySelectorAll(".users input.user-name")].pop()?.focus());
  },

  removeUser(id) {
    this.userRows = this.userRows.filter((u) => u.id !== id);
  },

  async load(keepMessage = false) {
    const s = await api("/api/settings");
    this.userRows = (s.users ?? []).map((u) => row(u.username, u.password));
    this.off = Object.fromEntries(PORTS.map((p) => [p.key, !p.required && !(s[p.key] > 0)]));
    this.model = s;
    this.saved = this.snapshot();
    this.externalChange = false;
    if (!keepMessage) this.message = "";
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
    this.saving = true;
    await this.$store.app.run(async () => {
      const result = await api("/api/settings", { method: "PUT", json: settings });
      let msg = "Configurações salvas.";
      if (result.servicesRestarted) msg += " Serviços reiniciados com as novas portas.";
      if (result.restartRequired) msg += " Feche e abra o aplicativo para aplicar as portas da interface web.";
      this.message = msg;
      this.$store.app.notify("Configurações salvas");
      if (result.status) this.$store.app.setStatus(result.status);
      await this.load(true);
    });
    this.saving = false;
  },

  async setTrust(trust, scope) {
    const messages = {
      "true-user": "Certificado marcado como confiável para o seu usuário",
      "true-computer": "Certificado marcado como confiável para todo o computador",
      "false-user": "Confiança removida do seu usuário",
      "false-computer": "Confiança removida do computador",
    };
    this.trustBusy = true;
    try {
      await this.$store.app.run(
        () => api(`/api/certificate/${trust ? "trust" : "untrust"}?scope=${scope}`, { method: "POST" }),
        messages[`${trust}-${scope}`],
      );
    } finally {
      this.trustBusy = false;
      this.$store.app.loadStatus();
    }
  },

  openFolder() {
    return this.$store.app.run(() => api("/api/open-data-folder", { method: "POST" }));
  },

  clearData() {
    if (!confirm("Apagar todos os e-mails, pastas criadas, tokens OAuth e o log de conexões? As configurações serão mantidas.")) return;
    return this.$store.app.run(async () => {
      const r = await api("/api/data/clear", { method: "POST" });
      this.$store.app.notify(`Dados limpos: ${r.messagesDeleted} e-mails e ${r.tokensDeleted} tokens apagados`);
    });
  },

  removeData() {
    const scope = this.$store.app.status?.certificate?.trustScope;
    const trustNote = {
      user: "\n\nO certificado também sai da lista de confiáveis do seu usuário; o Windows vai pedir uma confirmação.",
      computer: "\n\nO certificado também sai da lista de confiáveis do computador; o Windows vai pedir permissão de administrador.",
    }[scope] ?? "";
    const text = `Remover os dados deste computador?\n\nSerá apagado tudo o que o Fake Server Email gravou em:\n${this.dataDirectory}\n\n(e-mails, tokens, configurações, chave do OAuth e certificado)` +
      trustNote +
      "\n\nO programa será fechado. Não dá para desfazer.";
    if (!confirm(text)) return;
    return this.$store.app.run(async () => {
      const r = await api("/api/data/remove", { method: "POST" });
      this.$store.app.stop("Dados removidos", "Tudo o que o programa gravou foi apagado e o servidor foi fechado.", {
        path: r.dataDirectory,
        warning: r.warning,
        hint: "Se não for mais usar, basta apagar o FakeServerEmail.exe. Se abrir de novo, ele começa do zero.",
      });
    });
  },

  shutdown() {
    if (!confirm("Encerrar o Fake Server Email? Os serviços SMTP, POP3, IMAP e OAuth vão parar.")) return;
    return this.$store.app.run(async () => {
      await api("/api/shutdown", { method: "POST" });
      this.$store.app.stop("Servidor encerrado", "Abra o FakeServerEmail.exe novamente para voltar a usar.");
    });
  },
});

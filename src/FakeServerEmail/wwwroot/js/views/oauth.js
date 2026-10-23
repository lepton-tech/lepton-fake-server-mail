import { api } from "../lib/api.js";
import { debounce, fmtDate, fmtShort } from "../lib/format.js";

export default () => ({
  tokens: [],
  form: {
    user: "",
    scope: "https://outlook.office.com/SMTP.Send https://outlook.office.com/IMAP.AccessAsUser.All",
    lifetimeSeconds: "",
  },
  generated: "",
  now: Date.now(),

  init() {
    const quiet = () => this.active() && this.load();
    const changed = debounce(quiet, 200);
    window.addEventListener("fse-tokens", changed);
    window.addEventListener("fse-connected", changed);
    this.$watch("$store.app.view", quiet);
    quiet();
    setInterval(() => {
      if (this.active()) this.now = Date.now();
    }, 10000);
  },

  fmtDate,

  active() {
    return this.$store.app.view === "oauth";
  },

  get endpoints() {
    const o = this.$store.app.status?.oauth;
    if (!o) return [];
    return [
      ["Discovery (OIDC)", o.discovery],
      ["Authorize", o.authorize],
      ["Token", o.token],
      ["Token (HTTP)", o.httpToken],
      ["Device code", o.deviceCode],
      ["JWKS", o.jwks],
      ["UserInfo", o.userinfo],
      ["Authority estilo Microsoft", o.microsoftAuthority],
      ["Token estilo Microsoft", o.microsoftToken],
    ];
  },

  async load() {
    try {
      this.tokens = await api("/api/oauth/tokens");
      this.now = Date.now();
    } catch { }
  },

  status(t) {
    if (t.revoked) return { text: "revogado", cls: "bad" };
    if (new Date(t.expiresAt).getTime() < this.now) return { text: "expirado", cls: "warn" };
    return { text: "válido até " + fmtShort(t.expiresAt), cls: "ok" };
  },

  generate() {
    return this.$store.app.run(async () => {
      const t = await api("/api/oauth/tokens", {
        method: "POST",
        json: { user: this.form.user, scope: this.form.scope, lifetimeSeconds: parseInt(this.form.lifetimeSeconds || "0", 10) || null },
      });
      this.generated = t.accessToken;
    });
  },

  revoke(t) {
    return this.$store.app.run(() => api("/api/oauth/tokens/" + encodeURIComponent(t.jti), { method: "DELETE" }));
  },

  clear() {
    if (!confirm("Apagar o histórico de tokens e todos os refresh tokens?")) return;
    return this.$store.app.run(() => api("/api/oauth/tokens", { method: "DELETE" }));
  },
});

import { api } from "../lib/api.js";
import { debounce, fmtDate, fmtTime } from "../lib/format.js";

export default () => ({
  tokens: [],
  form: {
    user: "",
    scope: "https://outlook.office.com/SMTP.Send https://outlook.office.com/IMAP.AccessAsUser.All",
    lifetimeSeconds: "",
  },
  generated: "",

  init() {
    const changed = debounce(() => this.active() && this.load(), 200);
    window.addEventListener("fse-tokens", changed);
    window.addEventListener("fse-connected", changed);
    this.$watch("$store.app.view", () => this.active() && this.load());
    if (this.active()) this.load();
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
    } catch { }
  },

  status(t) {
    if (t.revoked) return { text: "revogado", cls: "bad" };
    if (new Date(t.expiresAt).getTime() < Date.now()) return { text: "expirado", cls: "warn" };
    return { text: "válido até " + fmtTime(t.expiresAt), cls: "ok" };
  },

  async generate() {
    try {
      const t = await api("/api/oauth/tokens", {
        method: "POST",
        json: { user: this.form.user, scope: this.form.scope, lifetimeSeconds: parseInt(this.form.lifetimeSeconds || "0", 10) || null },
      });
      this.generated = t.accessToken;
      this.load();
    } catch (err) {
      this.$store.app.notify(err.message, true);
    }
  },

  async revoke(t) {
    await api("/api/oauth/tokens/" + t.jti, { method: "DELETE" });
  },

  async clear() {
    if (!confirm("Apagar o histórico de tokens e todos os refresh tokens?")) return;
    await api("/api/oauth/tokens", { method: "DELETE" });
  },
});

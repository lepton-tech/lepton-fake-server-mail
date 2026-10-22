import { api } from "../lib/api.js";

const escapeHtml = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

export default () => ({
  form: {
    from: "remetente@teste.local",
    to: "destinatario@teste.local",
    subject: "Teste do Fake Server Email",
    text: "Olá! Esta mensagem foi enviada pelo botão \"Enviar teste\".",
    withHtml: true,
    attachSample: false,
  },
  sending: false,

  init() {
    window.addEventListener("fse-open-send", () => this.$refs.dialog.showModal());
  },

  close() {
    this.$refs.dialog.close();
  },

  async send() {
    const f = this.form;
    const html = f.withHtml
      ? `<div style="font-family:Segoe UI,Arial,sans-serif"><h2 style="color:#2f6fde">${escapeHtml(f.subject)}</h2><p>${escapeHtml(f.text).replace(/\n/g, "<br>")}</p><p style="color:#888;font-size:12px">Enviado pelo Fake Server Email</p></div>`
      : null;
    this.sending = true;
    try {
      await api("/api/send-test", { method: "POST", json: { from: f.from, to: f.to, subject: f.subject, text: f.text, html, attachSample: f.attachSample } });
      this.close();
      this.$store.app.notify("Mensagem enviada pelo SMTP local");
    } catch (err) {
      this.$store.app.notify(err.message, true);
    } finally {
      this.sending = false;
    }
  },
});

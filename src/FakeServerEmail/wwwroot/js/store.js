import { api } from "./lib/api.js";
import { debounce } from "./lib/format.js";

const TABS = [
  { id: "inbox", label: "Caixa de entrada" },
  { id: "oauth", label: "OAuth" },
  { id: "sessions", label: "Conexões" },
  { id: "connect", label: "Como conectar" },
  { id: "settings", label: "Configurações" },
];

export function createAppStore() {
  return {
    tabs: TABS,
    status: null,
    view: "inbox",
    anchor: null,
    stopped: null,
    toast: { text: "", error: false, visible: false },
    toastTimer: null,

    init() {
      try {
        const saved = sessionStorage.getItem("fse-view");
        if (TABS.some((t) => t.id === saved)) this.view = saved;
      } catch { }
      this.loadStatus();
      const reload = debounce(() => this.loadStatus(), 200);
      for (const name of ["fse-messages", "fse-settings", "fse-status", "fse-connected"])
        window.addEventListener(name, reload);
    },

    async loadStatus() {
      try {
        this.status = await api("/api/status");
      } catch { }
    },

    setView(view, anchor = null) {
      this.anchor = anchor;
      this.view = view;
      try { sessionStorage.setItem("fse-view", view); } catch { }
    },

    notify(text, error = false) {
      this.toast = { text, error, visible: true };
      clearTimeout(this.toastTimer);
      this.toastTimer = setTimeout(() => (this.toast.visible = false), error ? 6000 : 2500);
    },

    async copy(text) {
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        const area = document.createElement("textarea");
        area.value = text;
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
      }
      this.notify("Copiado");
    },

    stop(title, message, extra = {}) {
      this.stopped = { title, message, ...extra };
    },

    port(name) {
      const service = this.status?.services.find((s) => s.name === name);
      return service?.running ? service.port : null;
    },

    get unread() {
      return this.status?.counts.unread ?? 0;
    },

    get pills() {
      const st = this.status;
      if (!st) return [];
      const groups = ["SMTP", "POP3", "IMAP"].map((protocol) => {
        const items = st.services.filter((s) => s.protocol === protocol && s.configuredPort > 0);
        const running = items.filter((s) => s.running);
        return {
          label: protocol,
          ports: running.map((s) => s.port).join(" · ") || "—",
          state: running.length === 0 ? "off" : items.some((s) => !s.running || s.port !== s.configuredPort) ? "moved" : "",
          title: items.map((s) => `${s.name}: ${s.running ? "porta " + s.port : "parado"}${s.error ? " (" + s.error + ")" : ""}`).join("\n"),
          visible: items.length > 0,
        };
      }).filter((g) => g.visible);
      if (st.web.httpPort)
        groups.push({ label: "Web", ports: st.web.httpPort + (st.web.httpsPort ? " · " + st.web.httpsPort : ""), state: "", title: "Interface web e OAuth" });
      return groups;
    },

    get title() {
      if (this.stopped) return "Fake Server Email (encerrado)";
      return this.unread ? `(${this.unread}) Fake Server Email` : "Fake Server Email";
    },
  };
}

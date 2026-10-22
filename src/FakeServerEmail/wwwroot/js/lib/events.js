const NAMES = ["messages", "sessions", "tokens", "settings", "status"];

export function connectEvents(isStopped) {
  let retry = 1000;
  let source = null;

  const open = () => {
    if (isStopped()) return;
    source = new EventSource("/api/events");
    source.addEventListener("open", () => {
      retry = 1000;
      window.dispatchEvent(new CustomEvent("fse-connected"));
    });
    for (const name of NAMES)
      source.addEventListener(name, () => window.dispatchEvent(new CustomEvent("fse-" + name)));
    source.addEventListener("error", () => {
      source.close();
      if (isStopped()) return;
      setTimeout(open, retry);
      retry = Math.min(retry * 2, 10000);
    });
  };

  open();
  return () => source?.close();
}

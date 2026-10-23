const NAMES = ["messages", "sessions", "tokens", "settings", "status"];

const emit = (name) => window.dispatchEvent(new CustomEvent("fse-" + name));

export function connectEvents(isStopped) {
  let retry = 1000;
  let source = null;
  let lost = false;

  const open = () => {
    if (isStopped()) return;
    source = new EventSource("/api/events");
    source.addEventListener("open", () => {
      retry = 1000;
      if (lost) emit("connected");
      lost = false;
    });
    for (const name of NAMES)
      source.addEventListener(name, () => emit(name));
    source.addEventListener("error", () => {
      source.close();
      if (isStopped()) return;
      lost = true;
      emit("disconnected");
      setTimeout(open, retry);
      retry = Math.min(retry * 2, 10000);
    });
  };

  open();
  return () => source?.close();
}

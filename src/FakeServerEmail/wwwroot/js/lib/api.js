const OFFLINE = "Sem conexão com o servidor. Verifique se o Fake Server Email está aberto.";

function statusMessage(status) {
  if (status === 404) return "Não encontrado. Talvez já tenha sido apagado.";
  if (status >= 500) return `O servidor teve um problema (código ${status}). Tente de novo.`;
  return `O servidor recusou o pedido (código ${status}).`;
}

export async function api(path, options = {}) {
  const { json, ...rest } = options;
  const opts = { ...rest, headers: { ...rest.headers } };
  if (json !== undefined) {
    opts.body = JSON.stringify(json);
    opts.headers["Content-Type"] = "application/json";
  }
  let res;
  try {
    res = await fetch(path, opts);
  } catch (err) {
    if (err.name === "AbortError") throw err;
    throw new Error(OFFLINE);
  }
  if (!res.ok) {
    let message = statusMessage(res.status);
    try {
      const body = await res.json();
      message = body.detail || message;
    } catch { }
    throw new Error(message);
  }
  if (res.status === 204) return null;
  const type = res.headers.get("content-type") ?? "";
  return type.includes("json") ? res.json() : res.text();
}

export const withFolder = (path, folder) => `${path}?${new URLSearchParams({ folder })}`;

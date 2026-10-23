import Alpine from "../vendor/alpine-3.17.4.esm.min.js";
import { connectEvents } from "./lib/events.js";
import { createAppStore } from "./store.js";
import connect from "./views/connect.js";
import inbox from "./views/inbox.js";
import oauth from "./views/oauth.js";
import sendDialog from "./views/send-dialog.js";
import sessions from "./views/sessions.js";
import settings from "./views/settings.js";

async function includePartials() {
  const slots = [...document.querySelectorAll("[data-include]")];
  await Promise.all(slots.map(async (slot) => {
    const response = await fetch(slot.dataset.include);
    if (!response.ok) throw new Error(`Falha ao carregar ${slot.dataset.include}: ${response.status}`);
    slot.outerHTML = await response.text();
  }));
}

function showLoadError() {
  const message = document.createElement("p");
  message.className = "load-error";
  message.textContent = "Não foi possível carregar a tela do Fake Server Email. Feche o programa, abra de novo e recarregue esta página.";
  document.body.replaceChildren(message);
}

async function start() {
  try {
    await includePartials();
  } catch {
    showLoadError();
    return;
  }

  Alpine.store("app", createAppStore());
  Alpine.data("inbox", inbox);
  Alpine.data("sessions", sessions);
  Alpine.data("oauth", oauth);
  Alpine.data("connect", connect);
  Alpine.data("settings", settings);
  Alpine.data("sendDialog", sendDialog);

  window.Alpine = Alpine;
  connectEvents(() => Boolean(Alpine.store("app").stopped));
  Alpine.start();

  Alpine.effect(() => {
    document.title = Alpine.store("app").title;
  });
}

start();

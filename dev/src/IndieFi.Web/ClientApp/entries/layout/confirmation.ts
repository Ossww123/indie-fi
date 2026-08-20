import { getRequiredElement } from "../../shared/dom";

const dialog = getRequiredElement<HTMLDialogElement>("[data-confirmation-dialog]");
const title = getRequiredElement<HTMLElement>("[data-confirmation-title]", dialog);
const message = getRequiredElement<HTMLElement>("[data-confirmation-message]", dialog);
const confirmButton = getRequiredElement<HTMLButtonElement>("[data-confirmation-submit]", dialog);
const confirmedForms = new WeakSet<HTMLFormElement>();

let pendingForm: HTMLFormElement | null = null;
let pendingSubmitter: HTMLElement | null = null;

document.addEventListener("submit", (event) => {
  if (!(event.target instanceof HTMLFormElement)) {
    return;
  }

  const form = event.target;
  const confirmationMessage = form.dataset.confirmMessage;

  if (!confirmationMessage || confirmedForms.has(form)) {
    return;
  }

  if (typeof dialog.showModal !== "function") {
    if (!window.confirm(confirmationMessage)) {
      event.preventDefault();
    }

    return;
  }

  event.preventDefault();
  pendingForm = form;
  pendingSubmitter = event instanceof SubmitEvent ? event.submitter : null;
  title.textContent = form.dataset.confirmTitle ?? "계속할까요?";
  message.textContent = confirmationMessage;
  confirmButton.textContent = form.dataset.confirmLabel ?? "확인";

  const isDangerous = form.dataset.confirmVariant !== "primary";
  confirmButton.classList.toggle("button-danger", isDangerous);
  confirmButton.classList.toggle("button-primary", !isDangerous);
  dialog.returnValue = "cancel";
  dialog.showModal();
});

dialog.addEventListener("click", (event) => {
  if (event.target === dialog) {
    dialog.close("cancel");
  }
});

dialog.addEventListener("close", () => {
  const form = pendingForm;
  const submitter = pendingSubmitter;
  pendingForm = null;
  pendingSubmitter = null;

  if (dialog.returnValue !== "confirm" || form === null) {
    return;
  }

  confirmedForms.add(form);
  form.requestSubmit(submitter ?? undefined);
  confirmedForms.delete(form);
});

type SubmitControl = HTMLButtonElement | HTMLInputElement;

type PendingFormState = {
  form: HTMLFormElement;
  submitter: SubmitControl | null;
  originalContent: string | null;
};

const pendingForms = new WeakSet<HTMLFormElement>();
const pendingStates = new Set<PendingFormState>();

document.addEventListener("submit", (event) => {
  if (!(event.target instanceof HTMLFormElement)) {
    return;
  }

  const form = event.target;
  if (event.defaultPrevented || !shouldTrack(form)) {
    return;
  }

  if (pendingForms.has(form)) {
    event.preventDefault();
    return;
  }

  const submitter = getSubmitter(event);
  const state: PendingFormState = {
    form,
    submitter,
    originalContent: getControlContent(submitter),
  };

  pendingForms.add(form);
  pendingStates.add(state);
  form.dataset.submitPending = "true";
  form.setAttribute("aria-busy", "true");

  if (submitter !== null) {
    submitter.dataset.submitPendingButton = "true";
    submitter.setAttribute("aria-disabled", "true");
    setControlContent(submitter, submitter.dataset.pendingLabel ?? "처리 중…");
  }
});

window.addEventListener("pageshow", () => {
  for (const state of pendingStates) {
    reset(state);
  }

  pendingStates.clear();
});

function shouldTrack(form: HTMLFormElement): boolean {
  return form.method.toLowerCase() === "post"
    && form.getAttribute("method")?.toLowerCase() !== "dialog"
    && form.dataset.noSubmitState === undefined;
}

function getSubmitter(event: SubmitEvent | Event): SubmitControl | null {
  if (!(event instanceof SubmitEvent)) {
    return null;
  }

  return event.submitter instanceof HTMLButtonElement
      || event.submitter instanceof HTMLInputElement
    ? event.submitter
    : null;
}

function getControlContent(control: SubmitControl | null): string | null {
  if (control instanceof HTMLButtonElement) {
    return control.innerHTML;
  }

  return control?.value ?? null;
}

function setControlContent(control: SubmitControl, content: string): void {
  if (control instanceof HTMLButtonElement) {
    control.textContent = content;
    return;
  }

  control.value = content;
}

function reset(state: PendingFormState): void {
  pendingForms.delete(state.form);
  delete state.form.dataset.submitPending;
  state.form.removeAttribute("aria-busy");

  if (state.submitter === null) {
    return;
  }

  delete state.submitter.dataset.submitPendingButton;
  state.submitter.removeAttribute("aria-disabled");
  if (state.originalContent === null) {
    return;
  }

  if (state.submitter instanceof HTMLButtonElement) {
    state.submitter.innerHTML = state.originalContent;
  } else {
    state.submitter.value = state.originalContent;
  }
}

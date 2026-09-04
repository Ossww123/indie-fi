import { getRequiredElement } from "../../shared/dom";

const form = getRequiredElement<HTMLFormElement>("[data-key-drop-form]");

// This marker makes the page-specific module observable without changing form behavior.
form.dataset.clientReady = "true";

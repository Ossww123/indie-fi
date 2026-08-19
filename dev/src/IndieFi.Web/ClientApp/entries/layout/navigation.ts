import { getRequiredElement } from "../../shared/dom";

const toggle = getRequiredElement<HTMLButtonElement>("[data-navigation-toggle]");
const menu = getRequiredElement<HTMLElement>("[data-navigation-menu]");
const label = getRequiredElement<HTMLElement>("[data-navigation-label]", toggle);
const accountContainer = document.querySelector<HTMLElement>("[data-account-container]");
const accountToggle = accountContainer?.querySelector<HTMLButtonElement>("[data-account-toggle]");
const accountMenu = accountContainer?.querySelector<HTMLElement>("[data-account-menu]");

const setAccountExpanded = (isExpanded: boolean): void => {
  if (!accountToggle || !accountMenu) {
    return;
  }

  accountToggle.setAttribute("aria-expanded", String(isExpanded));
  accountMenu.classList.toggle("hidden", !isExpanded);
};

toggle.addEventListener("click", () => {
  const isExpanded = toggle.getAttribute("aria-expanded") === "true";

  toggle.setAttribute("aria-expanded", String(!isExpanded));
  menu.classList.toggle("hidden", isExpanded);
  menu.classList.toggle("flex", !isExpanded);
  label.textContent = isExpanded ? "메뉴 열기" : "메뉴 닫기";

  if (isExpanded) {
    setAccountExpanded(false);
  }
});

accountToggle?.addEventListener("click", () => {
  const isExpanded = accountToggle.getAttribute("aria-expanded") === "true";
  setAccountExpanded(!isExpanded);
});

document.addEventListener("click", (event) => {
  if (event.target instanceof Node && !accountContainer?.contains(event.target)) {
    setAccountExpanded(false);
  }
});

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape" || accountToggle?.getAttribute("aria-expanded") !== "true") {
    return;
  }

  setAccountExpanded(false);
  accountToggle.focus();
});

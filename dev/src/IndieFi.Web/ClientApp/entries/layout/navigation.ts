import { getRequiredElement } from "../../shared/dom";

const toggle = getRequiredElement<HTMLButtonElement>("[data-navigation-toggle]");
const menu = getRequiredElement<HTMLElement>("[data-navigation-menu]");
const label = getRequiredElement<HTMLElement>("[data-navigation-label]", toggle);
const navigationContainer = getRequiredElement<HTMLElement>("[data-navigation-container]");
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

const setNavigationExpanded = (isExpanded: boolean): void => {
  toggle.setAttribute("aria-expanded", String(isExpanded));
  menu.classList.toggle("hidden", !isExpanded);
  menu.classList.toggle("flex", isExpanded);
  label.textContent = isExpanded ? "메뉴 닫기" : "메뉴 열기";

  if (!isExpanded) {
    setAccountExpanded(false);
  }
};

toggle.addEventListener("click", () => {
  const isExpanded = toggle.getAttribute("aria-expanded") === "true";
  setNavigationExpanded(!isExpanded);
});

accountToggle?.addEventListener("click", () => {
  const isExpanded = accountToggle.getAttribute("aria-expanded") === "true";
  setAccountExpanded(!isExpanded);
});

document.addEventListener("click", (event) => {
  if (!(event.target instanceof Node)) {
    return;
  }

  if (!accountContainer?.contains(event.target)) {
    setAccountExpanded(false);
  }

  if (!navigationContainer.contains(event.target) && toggle.getAttribute("aria-expanded") === "true") {
    setNavigationExpanded(false);
  }
});

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") {
    return;
  }

  if (accountToggle?.getAttribute("aria-expanded") === "true") {
    setAccountExpanded(false);
    accountToggle.focus();
    return;
  }

  if (toggle.getAttribute("aria-expanded") === "true") {
    setNavigationExpanded(false);
    toggle.focus();
  }
});

window.matchMedia("(min-width: 640px)").addEventListener("change", (event) => {
  if (event.matches) {
    setNavigationExpanded(false);
  }
});

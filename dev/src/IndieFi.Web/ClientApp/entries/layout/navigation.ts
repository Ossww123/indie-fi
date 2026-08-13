import { getRequiredElement } from "../../shared/dom";

const toggle = getRequiredElement<HTMLButtonElement>("[data-navigation-toggle]");
const menu = getRequiredElement<HTMLElement>("[data-navigation-menu]");

toggle.addEventListener("click", () => {
  const isExpanded = toggle.getAttribute("aria-expanded") === "true";

  toggle.setAttribute("aria-expanded", String(!isExpanded));
  menu.classList.toggle("hidden", isExpanded);
});

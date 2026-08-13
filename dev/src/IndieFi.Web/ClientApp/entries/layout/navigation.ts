import { getRequiredElement } from "../../shared/dom";

const toggle = getRequiredElement<HTMLButtonElement>("[data-navigation-toggle]");
const menu = getRequiredElement<HTMLElement>("[data-navigation-menu]");
const label = getRequiredElement<HTMLElement>("[data-navigation-label]", toggle);

toggle.addEventListener("click", () => {
  const isExpanded = toggle.getAttribute("aria-expanded") === "true";

  toggle.setAttribute("aria-expanded", String(!isExpanded));
  menu.classList.toggle("hidden", isExpanded);
  menu.classList.toggle("flex", !isExpanded);
  label.textContent = isExpanded ? "메뉴 열기" : "메뉴 닫기";
});

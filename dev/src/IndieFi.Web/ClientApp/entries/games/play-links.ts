import { getRequiredElement } from "../../shared/dom";

const editor = getRequiredElement<HTMLElement>("[data-play-links]");
const list = getRequiredElement<HTMLElement>("[data-play-link-list]", editor);
const template = getRequiredElement<HTMLTemplateElement>("[data-play-link-template]", editor);
const addButton = getRequiredElement<HTMLButtonElement>("[data-play-link-add]", editor);
const maxLinks = Number(editor.dataset.maxLinks);

if (!Number.isInteger(maxLinks) || maxLinks < 1) {
  throw new Error("The play link editor requires a positive maximum link count.");
}

editor.addEventListener("click", (event) => {
  const target = event.target;

  if (!(target instanceof Element)) {
    return;
  }

  if (target.closest("[data-play-link-add]") !== null) {
    addLink();
    return;
  }

  const actionButton = target.closest<HTMLButtonElement>("[data-play-link-action]");
  const row = actionButton?.closest<HTMLElement>("[data-play-link-row]");
  if (actionButton === null || actionButton === undefined || row === null || row === undefined) {
    return;
  }

  switch (actionButton.dataset.playLinkAction) {
    case "up":
      row.previousElementSibling?.before(row);
      break;
    case "down":
      row.nextElementSibling?.after(row);
      break;
    case "remove":
      row.remove();
      break;
    default:
      return;
  }

  refreshRows();
});

refreshRows();
editor.dataset.clientReady = "true";

function addLink(): void {
  const rows = getRows();
  if (rows.length >= maxLinks) {
    return;
  }

  const fragment = template.content.cloneNode(true) as DocumentFragment;
  const newRow = getRequiredElement<HTMLElement>("[data-play-link-row]", fragment);
  list.append(fragment);
  refreshRows();
  getRequiredElement<HTMLInputElement>("input[name$='.DisplayName']", newRow).focus();
}

function refreshRows(): void {
  const rows = getRows();

  rows.forEach((row, index) => {
    reindexAttributes(row, index);

    const heading = getRequiredElement<HTMLElement>("[data-play-link-heading]", row);
    heading.textContent = `링크 ${index + 1}`;

    const upButton = getActionButton(row, "up");
    const downButton = getActionButton(row, "down");
    const removeButton = getActionButton(row, "remove");
    upButton.disabled = index === 0;
    downButton.disabled = index === rows.length - 1;
    upButton.setAttribute("aria-label", `링크 ${index + 1} 위로 이동`);
    downButton.setAttribute("aria-label", `링크 ${index + 1} 아래로 이동`);
    removeButton.setAttribute("aria-label", `링크 ${index + 1} 삭제`);
  });

  addButton.disabled = rows.length >= maxLinks;
}

function reindexAttributes(row: HTMLElement, index: number): void {
  const elements = [row, ...row.querySelectorAll<HTMLElement>("*")];

  for (const element of elements) {
    for (const attributeName of ["name", "id", "for", "data-valmsg-for"]) {
      const value = element.getAttribute(attributeName);
      if (value === null) {
        continue;
      }

      element.setAttribute(
        attributeName,
        value
          .replace(/Links\[(?:\d+|__INDEX__)\]/g, `Links[${index}]`)
          .replace(/Links_(?:\d+|__INDEX__)__/g, `Links_${index}__`),
      );
    }
  }
}

function getRows(): HTMLElement[] {
  return Array.from(list.querySelectorAll<HTMLElement>(":scope > [data-play-link-row]"));
}

function getActionButton(row: HTMLElement, action: string): HTMLButtonElement {
  return getRequiredElement<HTMLButtonElement>(
    `[data-play-link-action='${action}']`,
    row,
  );
}

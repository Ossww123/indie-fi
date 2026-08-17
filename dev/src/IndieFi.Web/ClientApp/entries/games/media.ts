import { getRequiredElement } from "../../shared/dom";
import {
  DirectMediaManager,
  readPositiveInteger,
  type MediaManagementAction,
} from "../../shared/direct-media-manager";

const root = getRequiredElement<HTMLElement>("[data-game-media]");
const gameId = readPositiveInteger(root.dataset.gameId, "game id");
const maximumFileBytes = readPositiveInteger(
  root.dataset.maxFileBytes,
  "maximum file bytes",
);
const manager = new DirectMediaManager({
  root,
  targetKind: "Game",
  targetId: gameId,
  maximumFileBytes,
  attachmentKinds: ["GameCover", "GameScreenshot"],
  onAction: handleManagementAction,
  onRefreshManagementButtons: refreshScreenshotButtons,
});

manager.initialize();

function handleManagementAction(
  action: MediaManagementAction,
  item: HTMLElement,
): void {
  switch (action) {
    case "up":
      void reorderScreenshot(item, -1);
      break;
    case "down":
      void reorderScreenshot(item, 1);
      break;
    case "remove":
      break;
  }
}

async function reorderScreenshot(
  item: HTMLElement,
  direction: -1 | 1,
): Promise<void> {
  const screenshots = getReadyScreenshots();
  const currentIndex = screenshots.indexOf(item);
  const targetIndex = currentIndex + direction;
  if (currentIndex < 0 || targetIndex < 0 || targetIndex >= screenshots.length) {
    manager.refreshManagementButtons();
    return;
  }

  const reordered = screenshots.slice();
  const target = reordered[targetIndex];
  if (target === undefined) {
    return;
  }
  reordered[currentIndex] = target;
  reordered[targetIndex] = item;

  manager.setManagementActionBusy(true);
  try {
    const antiforgery = await manager.getAntiforgery();
    await manager.sendJson<unknown>(
      manager.apiUrl(`games/${gameId}/screenshots/order`),
      "PUT",
      {
        mediaIds: reordered.map((screenshot) =>
          requireMediaId(screenshot.dataset.mediaId)),
      },
      antiforgery,
    );

    if (direction < 0) {
      target.before(item);
    } else {
      target.after(item);
    }
    manager.showNotice("스크린샷 순서를 저장했습니다.", "success");
  } catch (error: unknown) {
    manager.showError(error);
  } finally {
    manager.setManagementActionBusy(false);
  }
}

function getReadyScreenshots(): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(
    "[data-media-list='GameScreenshot'] [data-media-item][data-media-status='Ready']",
  ));
}

function refreshScreenshotButtons(busy: boolean): void {
  const screenshots = getReadyScreenshots();
  screenshots.forEach((item, index) => {
    item.dataset.mediaSortOrder = String(index);
    const badge = item.querySelector<HTMLElement>("[data-media-sort-badge]");
    if (badge !== null) {
      badge.textContent = `순서 ${index + 1}`;
    }

    const up = item.querySelector<HTMLButtonElement>("[data-media-action='up']");
    const down = item.querySelector<HTMLButtonElement>("[data-media-action='down']");
    if (up !== null) {
      up.disabled = busy || index === 0;
      up.setAttribute("aria-label", `스크린샷 ${index + 1} 위로 이동`);
    }
    if (down !== null) {
      down.disabled = busy || index === screenshots.length - 1;
      down.setAttribute("aria-label", `스크린샷 ${index + 1} 아래로 이동`);
    }
  });
}

function requireMediaId(value: string | undefined): string {
  if (value === undefined || value.length === 0) {
    throw new Error("The media page requires a media id.");
  }

  return value;
}

import { getRequiredElement } from "./dom";

export type MediaAttachmentKind = "GameCover" | "GameScreenshot" | "ProfileImage";
export type MediaManagementAction = "remove" | "up" | "down";

interface MediaAntiforgeryResponse {
  requestToken: string;
  headerName: string;
}

interface MediaReservationResponse {
  mediaId: string;
  status: string;
  uploadExpiresAt: string;
}

interface MediaUploadAttemptResponse {
  mediaId: string;
  uploadAttemptId: string;
  uploadUrl: string;
  urlExpiresAt: string;
  requiredContentType: string;
  requiredContentLength: number;
}

interface MediaStatusResponse {
  mediaId: string;
  status: string;
  failureCode: string | null;
}

interface MediaErrorResponse {
  code?: unknown;
  message?: unknown;
}

interface DirectMediaManagerOptions {
  root: HTMLElement;
  targetKind: "Game" | "Profile";
  targetId: number;
  maximumFileBytes: number;
  attachmentKinds: readonly MediaAttachmentKind[];
  onAction?: (
    action: MediaManagementAction,
    item: HTMLElement,
    manager: DirectMediaManager,
  ) => void;
  onRefreshManagementButtons?: (
    busy: boolean,
    manager: DirectMediaManager,
  ) => void;
}

type NoticeKind = "neutral" | "success" | "warning";

const allowedContentTypes = new Set(["image/jpeg", "image/png", "image/webp"]);
const terminalStatuses = new Set(["Ready", "Failed", "Expired"]);
const pollIntervalMilliseconds = 1_500;
const pollTimeoutMilliseconds = 120_000;

export class DirectMediaManager {
  private readonly notice: HTMLElement;
  private readonly apiBase: string;
  private antiforgeryPromise: Promise<MediaAntiforgeryResponse> | null = null;
  private managementActionBusy = false;

  public constructor(private readonly options: DirectMediaManagerOptions) {
    this.notice = getRequiredElement<HTMLElement>(
      "[data-media-notice]",
      options.root,
    );
    this.apiBase = requireDataset(
      options.root.dataset.mediaApiBase,
      "media API base",
    );
  }

  public initialize(): void {
    for (const form of this.options.root.querySelectorAll<HTMLFormElement>(
      "[data-media-upload-form]",
    )) {
      this.initializeUploadForm(form);
    }

    this.options.root.addEventListener("click", (event) => {
      this.handleManagementClick(event);
    });
    this.refreshManagementButtons();
    this.options.root.dataset.clientReady = "true";
  }

  public async getAntiforgery(): Promise<MediaAntiforgeryResponse> {
    this.antiforgeryPromise ??= fetch(`${this.apiBase}/antiforgery`, {
      method: "GET",
      credentials: "same-origin",
      cache: "no-store",
      headers: { Accept: "application/json" },
    }).then((response) => readJsonResponse<MediaAntiforgeryResponse>(response));
    return this.antiforgeryPromise;
  }

  public apiUrl(path: string): string {
    return `${this.apiBase}/${path.replace(/^\/+/, "")}`;
  }

  public async sendJson<T>(
    url: string,
    method: "POST" | "PUT" | "DELETE",
    body: unknown,
    antiforgery: MediaAntiforgeryResponse,
    signal?: AbortSignal,
  ): Promise<T> {
    const headers = new Headers({ Accept: "application/json" });
    headers.set(antiforgery.headerName, antiforgery.requestToken);
    const request: RequestInit = {
      method,
      headers,
      credentials: "same-origin",
      cache: "no-store",
    };
    if (body !== undefined) {
      headers.set("Content-Type", "application/json");
      request.body = JSON.stringify(body);
    }
    if (signal !== undefined) {
      request.signal = signal;
    }

    return readJsonResponse<T>(await fetch(url, request));
  }

  public showNotice(message: string, kind: NoticeKind): void {
    this.notice.textContent = message;
    this.notice.classList.remove(
      "hidden",
      "alert-neutral",
      "alert-success",
      "alert-warning",
    );
    this.notice.classList.add(`alert-${kind}`);
  }

  public showError(error: unknown): void {
    this.showNotice(toUserMessage(error), "warning");
  }

  public setManagementActionBusy(busy: boolean): void {
    this.managementActionBusy = busy;
    this.options.root.dataset.managementBusy = String(busy);
    this.refreshManagementButtons();
  }

  public refreshManagementButtons(): void {
    for (const button of this.options.root.querySelectorAll<HTMLButtonElement>(
      "[data-media-action]",
    )) {
      button.disabled = this.managementActionBusy;
    }
    this.options.onRefreshManagementButtons?.(this.managementActionBusy, this);
  }

  private initializeUploadForm(form: HTMLFormElement): void {
    const input = getRequiredElement<HTMLInputElement>("[data-media-file]", form);
    const button = getRequiredElement<HTMLButtonElement>(
      "[data-media-upload-submit]",
      form,
    );
    const cancelButton = getRequiredElement<HTMLButtonElement>(
      "[data-media-upload-cancel]",
      form,
    );
    const attachmentKind = this.parseAttachmentKind(form.dataset.attachmentKind);
    let controller: AbortController | null = null;

    button.disabled = false;
    button.addEventListener("click", () => {
      controller = new AbortController();
      void this.uploadSelectedFile(
        form,
        input,
        button,
        cancelButton,
        attachmentKind,
        controller.signal,
      ).finally(() => {
        controller = null;
      });
    });
    cancelButton.addEventListener("click", () => {
      cancelButton.disabled = true;
      controller?.abort();
    });
  }

  private handleManagementClick(event: Event): void {
    const target = event.target;
    if (!(target instanceof Element)) {
      return;
    }

    const button = target.closest<HTMLButtonElement>("[data-media-action]");
    const item = button?.closest<HTMLElement>("[data-media-item]");
    if (button === null || button === undefined
      || item === null || item === undefined
      || button.disabled || this.managementActionBusy) {
      return;
    }

    const action = parseManagementAction(button.dataset.mediaAction);
    if (action === "remove") {
      void this.removeMedia(item);
      return;
    }

    this.options.onAction?.(action, item, this);
  }

  private async uploadSelectedFile(
    form: HTMLFormElement,
    input: HTMLInputElement,
    button: HTMLButtonElement,
    cancelButton: HTMLButtonElement,
    attachmentKind: MediaAttachmentKind,
    signal: AbortSignal,
  ): Promise<void> {
    if (form.dataset.busy === "true") {
      return;
    }

    const file = input.files?.item(0) ?? null;
    const validationMessage = this.validateFile(file);
    if (validationMessage !== null || file === null) {
      this.showNotice(
        validationMessage ?? "업로드할 파일을 선택해 주세요.",
        "warning",
      );
      input.focus();
      return;
    }

    let reservationId: string | null = null;
    let antiforgery: MediaAntiforgeryResponse | null = null;
    setBusy(form, button, cancelButton, true);
    try {
      this.showNotice("업로드 공간을 예약하고 있습니다.", "neutral");
      antiforgery = await this.getAntiforgery();
      const reservation = await this.sendJson<MediaReservationResponse>(
        `${this.apiBase}/reservations`,
        "POST",
        {
          targetKind: this.options.targetKind,
          targetId: this.options.targetId,
          attachmentKind,
          contentType: file.type,
          contentLength: file.size,
        },
        antiforgery,
        signal,
      );
      reservationId = reservation.mediaId;

      this.showNotice("이미지를 저장소로 전송하고 있습니다.", "neutral");
      const uploadAttempt = await this.uploadWithOneReissue(
        reservation.mediaId,
        file,
        antiforgery,
        signal,
      );

      this.showNotice(
        "업로드를 확인하고 이미지 처리를 요청하고 있습니다.",
        "neutral",
      );
      await this.sendJson<unknown>(
        `${this.apiBase}/${encodeURIComponent(reservation.mediaId)}`
          + `/upload-attempts/${encodeURIComponent(uploadAttempt.uploadAttemptId)}/complete`,
        "POST",
        undefined,
        antiforgery,
        signal,
      );

      const status = await this.waitForTerminalStatus(reservation.mediaId, signal);
      if (status.status === "Ready") {
        this.showNotice(
          "이미지가 준비되었습니다. 최신 상태를 불러옵니다.",
          "success",
        );
      } else if (status.status === "Failed") {
        const failure = status.failureCode === null
          ? "알 수 없는 처리 오류"
          : status.failureCode;
        this.showNotice(
          `이미지 처리에 실패했습니다. 오류 코드: ${failure}`,
          "warning",
        );
      } else {
        this.showNotice(
          "업로드 예약이 만료되었습니다. 파일을 다시 선택해 주세요.",
          "warning",
        );
      }

      input.value = "";
      window.setTimeout(() => window.location.reload(), 800);
    } catch (error: unknown) {
      if (signal.aborted) {
        if (reservationId !== null && antiforgery !== null) {
          await this.releaseCancelledReservation(reservationId, antiforgery);
        }
        this.showNotice("업로드를 취소했습니다.", "neutral");
      } else {
        this.showNotice(toUserMessage(error), "warning");
      }
    } finally {
      setBusy(form, button, cancelButton, false);
    }
  }

  private validateFile(file: File | null): string | null {
    if (file === null) {
      return "업로드할 파일을 선택해 주세요.";
    }

    if (!allowedContentTypes.has(file.type)) {
      return "JPEG, PNG 또는 WebP 이미지만 업로드할 수 있습니다.";
    }

    if (file.size <= 0) {
      return "비어 있는 파일은 업로드할 수 없습니다.";
    }

    if (file.size > this.options.maximumFileBytes) {
      return `이미지는 파일당 ${formatMaximumBytes(this.options.maximumFileBytes)} 이하여야 합니다.`;
    }

    return null;
  }

  private async uploadWithOneReissue(
    mediaId: string,
    file: File,
    antiforgery: MediaAntiforgeryResponse,
    signal: AbortSignal,
  ): Promise<MediaUploadAttemptResponse> {
    let lastFailure: unknown = null;
    for (let attemptNumber = 0; attemptNumber < 2; attemptNumber += 1) {
      const uploadAttempt = await this.sendJson<MediaUploadAttemptResponse>(
        `${this.apiBase}/${encodeURIComponent(mediaId)}/upload-attempts`,
        "POST",
        undefined,
        antiforgery,
        signal,
      );
      assertUploadContract(uploadAttempt, mediaId, file);

      try {
        const response = await fetch(uploadAttempt.uploadUrl, {
          method: "PUT",
          headers: { "Content-Type": uploadAttempt.requiredContentType },
          body: file,
          credentials: "omit",
          cache: "no-store",
          referrerPolicy: "no-referrer",
          signal,
        });
        if (response.ok) {
          return uploadAttempt;
        }

        lastFailure = new Error(
          `Object storage returned HTTP ${response.status}.`,
        );
      } catch (error: unknown) {
        lastFailure = error;
      }

      if (attemptNumber === 0) {
        this.showNotice(
          "전송 주소를 새로 발급해 한 번 더 시도합니다.",
          "neutral",
        );
      }
    }

    throw new MediaClientError(
      "storage_upload_failed",
      "이미지 전송에 실패했습니다. 잠시 후 다시 시도해 주세요.",
      lastFailure,
    );
  }

  private async waitForTerminalStatus(
    mediaId: string,
    signal: AbortSignal,
  ): Promise<MediaStatusResponse> {
    const deadline = Date.now() + pollTimeoutMilliseconds;
    let transientFailureCount = 0;
    while (Date.now() < deadline) {
      await delay(pollIntervalMilliseconds);
      signal.throwIfAborted();
      try {
        const response = await fetch(
          `${this.apiBase}/${encodeURIComponent(mediaId)}`,
          {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store",
            headers: { Accept: "application/json" },
            signal,
          },
        );
        const status = await readJsonResponse<MediaStatusResponse>(response);
        transientFailureCount = 0;
        if (terminalStatuses.has(status.status)) {
          return status;
        }

        this.showNotice("이미지를 안전하게 처리하고 있습니다.", "neutral");
      } catch (error: unknown) {
        transientFailureCount += 1;
        if (transientFailureCount >= 3) {
          throw error;
        }
      }
    }

    throw new MediaClientError(
      "processing_timeout",
      "처리가 계속되고 있습니다. 잠시 후 페이지를 새로고침해 확인해 주세요.",
    );
  }

  private async releaseCancelledReservation(
    mediaId: string,
    antiforgery: MediaAntiforgeryResponse,
  ): Promise<void> {
    try {
      await this.sendJson<unknown>(
        `${this.apiBase}/${encodeURIComponent(mediaId)}`,
        "DELETE",
        undefined,
        antiforgery,
      );
    } catch {
      // The reservation still expires server-side. Cancellation must not replace
      // the useful user-facing message with a best-effort cleanup failure.
    }
  }

  private async removeMedia(item: HTMLElement): Promise<void> {
    const mediaId = requireDataset(item.dataset.mediaId, "media id");
    if (!window.confirm(
      "이 이미지를 제거하시겠습니까? 공개 중인 이미지는 즉시 화면에서 내려갑니다.",
    )) {
      return;
    }

    this.setManagementActionBusy(true);
    try {
      const antiforgery = await this.getAntiforgery();
      await this.sendJson<unknown>(
        `${this.apiBase}/${encodeURIComponent(mediaId)}`,
        "DELETE",
        undefined,
        antiforgery,
      );
      item.dataset.mediaStatus = "DeletePending";
      this.showNotice(
        "이미지를 공개에서 내리고 삭제를 요청했습니다.",
        "success",
      );
      window.setTimeout(() => window.location.reload(), 600);
    } catch (error: unknown) {
      this.showNotice(toUserMessage(error), "warning");
      this.setManagementActionBusy(false);
    }
  }

  private parseAttachmentKind(value: string | undefined): MediaAttachmentKind {
    const attachmentKind = this.options.attachmentKinds.find(
      (candidate) => candidate === value,
    );
    if (attachmentKind !== undefined) {
      return attachmentKind;
    }

    throw new Error("The media upload form has an invalid attachment kind.");
  }
}

export function readPositiveInteger(
  value: string | undefined,
  name: string,
): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed <= 0) {
    throw new Error(`The media page requires a positive ${name}.`);
  }

  return parsed;
}

function parseManagementAction(
  value: string | undefined,
): MediaManagementAction {
  if (value === "remove" || value === "up" || value === "down") {
    return value;
  }

  throw new Error("The media page contains an unsupported management action.");
}

function assertUploadContract(
  attempt: MediaUploadAttemptResponse,
  mediaId: string,
  file: File,
): void {
  if (attempt.mediaId !== mediaId
    || attempt.requiredContentType !== file.type
    || attempt.requiredContentLength !== file.size) {
    throw new MediaClientError(
      "upload_contract_mismatch",
      "서버가 다른 파일 조건을 반환했습니다. 페이지를 새로고침해 주세요.",
    );
  }
}

async function readJsonResponse<T>(response: Response): Promise<T> {
  let payload: unknown;
  try {
    payload = await response.json();
  } catch (error: unknown) {
    throw new MediaClientError(
      "invalid_server_response",
      "서버 응답을 확인할 수 없습니다. 잠시 후 다시 시도해 주세요.",
      error,
    );
  }

  if (!response.ok) {
    const apiError = isRecord(payload) ? payload as MediaErrorResponse : {};
    const code = typeof apiError.code === "string"
      ? apiError.code
      : "media_request_failed";
    throw new MediaClientError(code, messageForCode(code));
  }

  return payload as T;
}

function messageForCode(code: string): string {
  switch (code) {
    case "invalid_upload":
    case "invalid_media_reservation":
      return "파일 형식이나 크기를 확인해 주세요.";
    case "pending_upload_limit_reached":
      return "처리 중인 업로드가 많습니다. 기존 작업이 끝난 뒤 다시 시도해 주세요.";
    case "attachment_limit_reached":
      return "이미지 개수 제한에 도달했습니다.";
    case "replacement_in_progress":
      return "이미 다른 이미지 교체가 진행 중입니다.";
    case "media_capacity_exceeded":
      return "이미지 저장 용량 제한에 도달했습니다.";
    case "upload_short_window_limit_reached":
    case "upload_daily_limit_reached":
      return "업로드 요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.";
    case "media_storage_unavailable":
    case "upload_rate_limiter_unavailable":
      return "미디어 서비스가 일시적으로 응답하지 않습니다.";
    case "account_unavailable":
      return "현재 계정으로는 미디어를 변경할 수 없습니다.";
    case "media_not_found":
      return "업로드 항목을 찾을 수 없습니다. 페이지를 새로고침해 주세요.";
    case "invalid_screenshot_order":
      return "스크린샷 구성이 변경되었습니다. 페이지를 새로고침해 주세요.";
    case "media_not_removable":
      return "현재 처리 상태에서는 이미지를 제거할 수 없습니다.";
    case "antiforgery_validation_failed":
      return "보안 확인이 만료되었습니다. 페이지를 새로고침해 주세요.";
    default:
      return "업로드를 완료하지 못했습니다. 잠시 후 다시 시도해 주세요.";
  }
}

function setBusy(
  form: HTMLFormElement,
  button: HTMLButtonElement,
  cancelButton: HTMLButtonElement,
  busy: boolean,
): void {
  form.dataset.busy = String(busy);
  form.setAttribute("aria-busy", String(busy));
  button.disabled = busy;
  cancelButton.disabled = !busy;
  cancelButton.classList.toggle("hidden", !busy);
}

function requireDataset(value: string | undefined, name: string): string {
  if (value === undefined || value.length === 0) {
    throw new Error(`The media page requires ${name}.`);
  }

  return value;
}

function formatMaximumBytes(bytes: number): string {
  const mebibytes = bytes / (1024 * 1024);
  return Number.isInteger(mebibytes)
    ? `${mebibytes} MiB`
    : `${bytes} bytes`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

function toUserMessage(error: unknown): string {
  if (error instanceof MediaClientError) {
    return error.message;
  }

  return "네트워크 오류로 업로드를 완료하지 못했습니다. 다시 시도해 주세요.";
}

class MediaClientError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
    options?: unknown,
  ) {
    super(message, options === undefined ? undefined : { cause: options });
    this.name = "MediaClientError";
  }
}

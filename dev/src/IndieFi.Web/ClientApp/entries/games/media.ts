import { getRequiredElement } from "../../shared/dom";

type GameAttachmentKind = "GameCover" | "GameScreenshot";
type NoticeKind = "neutral" | "success" | "warning";

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

const allowedContentTypes = new Set(["image/jpeg", "image/png", "image/webp"]);
const terminalStatuses = new Set(["Ready", "Failed", "Expired"]);
const pollIntervalMilliseconds = 1_500;
const pollTimeoutMilliseconds = 120_000;

const root = getRequiredElement<HTMLElement>("[data-game-media]");
const notice = getRequiredElement<HTMLElement>("[data-media-notice]", root);
const gameId = readPositiveInteger(root.dataset.gameId, "game id");
const maximumFileBytes = readPositiveInteger(
  root.dataset.maxFileBytes,
  "maximum file bytes",
);
const apiBase = requireDataset(root.dataset.mediaApiBase, "media API base");
let antiforgeryPromise: Promise<MediaAntiforgeryResponse> | null = null;

for (const form of root.querySelectorAll<HTMLFormElement>("[data-media-upload-form]")) {
  const input = getRequiredElement<HTMLInputElement>("[data-media-file]", form);
  const button = getRequiredElement<HTMLButtonElement>("[data-media-upload-submit]", form);
  const cancelButton = getRequiredElement<HTMLButtonElement>(
    "[data-media-upload-cancel]",
    form,
  );
  const attachmentKind = parseAttachmentKind(form.dataset.attachmentKind);
  let controller: AbortController | null = null;

  button.disabled = false;
  button.addEventListener("click", () => {
    controller = new AbortController();
    void uploadSelectedFile(
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

root.dataset.clientReady = "true";

async function uploadSelectedFile(
  form: HTMLFormElement,
  input: HTMLInputElement,
  button: HTMLButtonElement,
  cancelButton: HTMLButtonElement,
  attachmentKind: GameAttachmentKind,
  signal: AbortSignal,
): Promise<void> {
  if (form.dataset.busy === "true") {
    return;
  }

  const file = input.files?.item(0) ?? null;
  const validationMessage = validateFile(file);
  if (validationMessage !== null || file === null) {
    showNotice(validationMessage ?? "업로드할 파일을 선택해 주세요.", "warning");
    input.focus();
    return;
  }

  let reservationId: string | null = null;
  let antiforgery: MediaAntiforgeryResponse | null = null;
  setBusy(form, button, cancelButton, true);
  try {
    showNotice("업로드 공간을 예약하고 있습니다.", "neutral");
    antiforgery = await getAntiforgery();
    const reservation = await sendJson<MediaReservationResponse>(
      `${apiBase}/reservations`,
      "POST",
      {
        targetKind: "Game",
        targetId: gameId,
        attachmentKind,
        contentType: file.type,
        contentLength: file.size,
      },
      antiforgery,
      signal,
    );
    reservationId = reservation.mediaId;

    showNotice("이미지를 저장소로 전송하고 있습니다.", "neutral");
    const uploadAttempt = await uploadWithOneReissue(
      reservation.mediaId,
      file,
      antiforgery,
      signal,
    );

    showNotice("업로드를 확인하고 이미지 처리를 요청하고 있습니다.", "neutral");
    await sendJson<unknown>(
      `${apiBase}/${encodeURIComponent(reservation.mediaId)}`
        + `/upload-attempts/${encodeURIComponent(uploadAttempt.uploadAttemptId)}/complete`,
      "POST",
      undefined,
      antiforgery,
      signal,
    );

    const status = await waitForTerminalStatus(reservation.mediaId, signal);
    if (status.status === "Ready") {
      showNotice("이미지가 준비되었습니다. 최신 상태를 불러옵니다.", "success");
    } else if (status.status === "Failed") {
      const failure = status.failureCode === null
        ? "알 수 없는 처리 오류"
        : status.failureCode;
      showNotice(`이미지 처리에 실패했습니다. 오류 코드: ${failure}`, "warning");
    } else {
      showNotice("업로드 예약이 만료되었습니다. 파일을 다시 선택해 주세요.", "warning");
    }

    input.value = "";
    window.setTimeout(() => window.location.reload(), 800);
  } catch (error: unknown) {
    if (signal.aborted) {
      if (reservationId !== null && antiforgery !== null) {
        await releaseCancelledReservation(reservationId, antiforgery);
      }
      showNotice("업로드를 취소했습니다.", "neutral");
    } else {
      showNotice(toUserMessage(error), "warning");
    }
  } finally {
    setBusy(form, button, cancelButton, false);
  }
}

function validateFile(file: File | null): string | null {
  if (file === null) {
    return "업로드할 파일을 선택해 주세요.";
  }

  if (!allowedContentTypes.has(file.type)) {
    return "JPEG, PNG 또는 WebP 이미지만 업로드할 수 있습니다.";
  }

  if (file.size <= 0) {
    return "비어 있는 파일은 업로드할 수 없습니다.";
  }

  if (file.size > maximumFileBytes) {
    return "이미지는 파일당 10 MiB 이하여야 합니다.";
  }

  return null;
}

async function uploadWithOneReissue(
  mediaId: string,
  file: File,
  antiforgery: MediaAntiforgeryResponse,
  signal: AbortSignal,
): Promise<MediaUploadAttemptResponse> {
  let lastFailure: unknown = null;
  for (let attemptNumber = 0; attemptNumber < 2; attemptNumber += 1) {
    const uploadAttempt = await sendJson<MediaUploadAttemptResponse>(
      `${apiBase}/${encodeURIComponent(mediaId)}/upload-attempts`,
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

      lastFailure = new Error(`Object storage returned HTTP ${response.status}.`);
    } catch (error: unknown) {
      lastFailure = error;
    }

    if (attemptNumber === 0) {
      showNotice("전송 주소를 새로 발급해 한 번 더 시도합니다.", "neutral");
    }
  }

  throw new MediaClientError(
    "storage_upload_failed",
    "이미지 전송에 실패했습니다. 잠시 후 다시 시도해 주세요.",
    lastFailure,
  );
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

async function waitForTerminalStatus(
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
        `${apiBase}/${encodeURIComponent(mediaId)}`,
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

      showNotice("이미지를 안전하게 처리하고 있습니다.", "neutral");
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

async function getAntiforgery(): Promise<MediaAntiforgeryResponse> {
  antiforgeryPromise ??= fetch(`${apiBase}/antiforgery`, {
    method: "GET",
    credentials: "same-origin",
    cache: "no-store",
    headers: { Accept: "application/json" },
  }).then((response) => readJsonResponse<MediaAntiforgeryResponse>(response));
  return antiforgeryPromise;
}

async function sendJson<T>(
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

async function releaseCancelledReservation(
  mediaId: string,
  antiforgery: MediaAntiforgeryResponse,
): Promise<void> {
  try {
    await sendJson<unknown>(
      `${apiBase}/${encodeURIComponent(mediaId)}`,
      "DELETE",
      undefined,
      antiforgery,
    );
  } catch {
    // The reservation still expires server-side. Cancellation must not replace
    // the useful user-facing message with a best-effort cleanup failure.
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
      return "이 게임의 이미지 개수 제한에 도달했습니다.";
    case "replacement_in_progress":
      return "이미 다른 대표 이미지 교체가 진행 중입니다.";
    case "media_capacity_exceeded":
      return "이 게임의 이미지 저장 용량 제한에 도달했습니다.";
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
    case "antiforgery_validation_failed":
      return "보안 확인이 만료되었습니다. 페이지를 새로고침해 주세요.";
    default:
      return "업로드를 완료하지 못했습니다. 잠시 후 다시 시도해 주세요.";
  }
}

function showNotice(message: string, kind: NoticeKind): void {
  notice.textContent = message;
  notice.classList.remove("hidden", "alert-neutral", "alert-success", "alert-warning");
  notice.classList.add(`alert-${kind}`);
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

function parseAttachmentKind(value: string | undefined): GameAttachmentKind {
  if (value === "GameCover" || value === "GameScreenshot") {
    return value;
  }

  throw new Error("The media upload form has an invalid attachment kind.");
}

function readPositiveInteger(value: string | undefined, name: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed <= 0) {
    throw new Error(`The media page requires a positive ${name}.`);
  }

  return parsed;
}

function requireDataset(value: string | undefined, name: string): string {
  if (value === undefined || value.length === 0) {
    throw new Error(`The media page requires ${name}.`);
  }

  return value;
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

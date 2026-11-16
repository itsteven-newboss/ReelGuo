/**
 * Typed client for the ReelGuo API. `schema.d.ts` is generated from the backend's OpenAPI
 * document (`pnpm gen:api`); never edit it by hand. CI fails if it is out of date.
 */
import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type Schemas = components["schemas"];
export type ApiErrorBody = Schemas["ApiErrorResponse"];

export type ReelGuoClient = ReturnType<typeof createReelGuoClient>;

export interface ClientOptions {
  /** Defaults to same-origin: the web app and API share a domain (`/api/*` is routed to ASP.NET). */
  baseUrl?: string;
  /** Extra headers on every request, e.g. the admin key. Called per request so it can change at runtime. */
  headers?: () => Record<string, string | undefined>;
  /** Called with the HTTP status whenever the API answers with an error. */
  onError?: (status: number, body: ApiErrorBody | undefined) => void;
}

export function createReelGuoClient(options: ClientOptions = {}) {
  const client = createClient<paths>({ baseUrl: options.baseUrl ?? "" });

  const middleware: Middleware = {
    onRequest({ request }) {
      for (const [name, value] of Object.entries(options.headers?.() ?? {})) {
        if (value) request.headers.set(name, value);
      }
      return request;
    },
    async onResponse({ response }) {
      if (!response.ok && options.onError) {
        const body = (await response.clone().json().catch(() => undefined)) as ApiErrorBody | undefined;
        options.onError(response.status, body);
      }
      return response;
    },
  };
  client.use(middleware);
  return client;
}

/** Error thrown by `unwrap` so React Query and UI code can switch on `code`. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    /** Extra messages, e.g. every unmet publish requirement. */
    readonly details: readonly string[] = [],
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Turns an openapi-fetch result into data-or-throw. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.response.ok) return result.data as T;
  const body = result.error as ApiErrorBody | undefined;
  throw new ApiError(
    result.response.status,
    body?.error?.code ?? `http_${result.response.status}`,
    body?.error?.message ?? `请求失败（HTTP ${result.response.status}）`,
    body?.error?.details ?? [],
  );
}

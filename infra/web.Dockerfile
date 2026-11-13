# Builds the app and admin console, then serves both with Caddy. Build context: repo root.
FROM node:24-alpine AS build
WORKDIR /repo
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY edge/package.json edge/
COPY web/ web/
RUN pnpm install --frozen-lockfile
RUN pnpm --filter "./web/**" build

FROM caddy:2-alpine
COPY infra/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /repo/web/apps/client/dist /srv/client
COPY --from=build /repo/web/apps/admin/dist /srv/admin
EXPOSE 8080 8081

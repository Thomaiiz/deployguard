# syntax=docker/dockerfile:1

# ---- Etapa 1: build do Angular ----
FROM node:26-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
# npm ci instala exatamente o que está no package-lock.json (build reprodutível).
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

# ---- Etapa 2: arquivos estáticos servidos pelo nginx (sem Node em produção) ----
FROM nginxinc/nginx-unprivileged:1.31-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/frontend/browser /usr/share/nginx/html
EXPOSE 8080

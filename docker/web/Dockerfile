# BlinkyLite web console behind nginx (0055).
#
# Build from the repository root:
#   docker build -f docker/web/Dockerfile -t blinkylite-web .

FROM node:24-alpine AS build
WORKDIR /src

# The message catalogue is the C# resx files; the web build turns them into
# JSON (web/scripts/messages.mjs), so they come along.
COPY src/BlinkyLite.Contracts/Resources/ src/BlinkyLite.Contracts/Resources/
COPY web/package.json web/package-lock.json web/
RUN cd web && npm ci --no-audit --no-fund

COPY web/ web/
RUN cd web && npm run build

FROM nginx:1.29-alpine

# The image fills ${...} in /etc/nginx/templates/*.template from the
# environment at start; the proxy snippet needs the same treatment, so it is a
# template too and lands in /etc/nginx/.
ENV NGINX_ENVSUBST_OUTPUT_DIR=/etc/nginx
COPY docker/web/nginx.conf.template /etc/nginx/templates/conf.d/default.conf.template
COPY docker/web/blinkylite-proxy.conf /etc/nginx/templates/blinkylite-proxy.conf.template
COPY --from=build /src/web/dist/web/browser /usr/share/nginx/html

EXPOSE 443 80

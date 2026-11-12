ARG NODE_VERSION=26
FROM node:${NODE_VERSION}-trixie-slim

# Docker copies these permissions into fresh named volumes. The host UID can
# then install dependencies without running npm as root or changing host owners.
RUN mkdir -p /app/node_modules /npm-cache \
    && chmod 1777 /app/node_modules /npm-cache

WORKDIR /app
USER node

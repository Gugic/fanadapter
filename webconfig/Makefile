.DEFAULT_GOAL := help

COMPOSE ?= docker compose
export COMPOSE_PROJECT_NAME ?= fanadapter-webconfig
export NODE_VERSION = $(shell cat .node-version 2>/dev/null)
export LOCAL_UID := $(shell id -u)
export LOCAL_GID := $(shell id -g)

.PHONY: help check dev preview config stop reset

help:
	@printf '%s\n' \
	  'Docker development (run from webconfig/, or use make -C webconfig):' \
	  '  make check    Install locked dependencies and run the CI checks' \
	  '  make dev      Start http://localhost:5173/fanadapter/' \
	  '  make preview  Build and serve http://localhost:4173/fanadapter/' \
	  '  make config   Show the resolved Docker Compose configuration' \
	  '  make stop     Stop containers belonging to this project' \
	  '  make reset    Stop this project and remove its dependency/cache volumes' \
	  'Set COMPOSE_PROJECT_NAME to isolate another checkout.' \
	  'Override DEV_PORT or PREVIEW_PORT if the default port is occupied.'

check:
	$(COMPOSE) run --build --rm check

dev:
	$(COMPOSE) run --build --rm --service-ports dev

preview:
	$(COMPOSE) run --build --rm --service-ports preview

config:
	$(COMPOSE) --profile '*' config

stop:
	$(COMPOSE) --profile '*' down --remove-orphans

reset:
	$(COMPOSE) --profile '*' down --remove-orphans --volumes

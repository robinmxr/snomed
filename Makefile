# Usage:
#   cp .env.example .env     # then edit
#   make up                  # start Postgres (Docker)
#   make migrate             # schema, extensions, tables, indexes
#   make load PATH_TO_RELEASE=/path/to/SnomedCT_release   # load YOUR download and build the search table
#   make test                # run the test suite (needs only Docker)
SHELL := /bin/bash
# db/*.sh run psql inside the compose service, so no local psql install is needed.
export SNOMED_USE_DOCKER ?= 1

.PHONY: up stop migrate load test build

up:
	docker compose up -d --wait db

stop:
	docker compose stop db

migrate:
	set -a; . ./.env; set +a; db/migrate.sh

load:
	@test -n "$(PATH_TO_RELEASE)" || (echo "usage: make load PATH_TO_RELEASE=/path/to/release" && exit 1)
	set -a; . ./.env; set +a; db/load.sh "$(PATH_TO_RELEASE)"

build:
	dotnet build src/SnomedSearch.slnx

test:
	dotnet test src/SnomedSearch.slnx

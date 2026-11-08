#!/bin/sh
# Says what a first build, test or run still needs on this machine, one line per check, and changes
# nothing (it installs, trusts, starts and stops nothing):
#   ok       the check passed
#   missing  something is absent or in the way, and the line names the fix; "optional" when a first
#            run does without it
#   skip     the check cannot run here, and the line says why
# Exits 1 when something a first run needs is missing, 0 otherwise. The checks:
#   .NET SDK         one on PATH that global.json accepts (its version within its rollForward band);
#                    without one, ./scripts/dotnet.sh builds and tests in the SDK container, but
#                    aspire run needs a local SDK (ADR-0008)
#   Docker           the engine answers (docker info), with the CLI found as scripts/dotnet.sh finds it
#                    (DOCKER_BIN, then Docker Desktop's ~/.docker/bin)
#   SDK image        the image scripts/dotnet.sh runs (its name read from that script, its override
#                    variable honoured), and the one line that builds it
#   Aspire CLI       13.5 or later, for aspire run and the aspire MCP server in .mcp.json (optional:
#                    dotnet run --project on the AppHost starts the same app)
#   dev certificate  trusted, for the AppHost's https profile (dotnet dev-certs https --check --trust;
#                    with a local SDK only)
#   AppHost ports    every localhost port tools/*.AppHost/Properties/launchSettings.json names (each
#                    applicationUrl and every *_URL or *_URLS value) is free: lsof names a holder, and
#                    a connection (nc -z, else bash's /dev/tcp) finds one lsof cannot list; reported,
#                    never freed
#   Testcontainers   the engine endpoint scripts/dotnet.sh hands the tests: DOCKER_HOST,
#                    TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE and, inside a container such as the dev
#                    container, TESTCONTAINERS_HOST_OVERRIDE (docs/development.md, "Docker engines
#                    other than Docker Desktop")
#   git, jq          git for the docs-drift and ADR checks and the commit hook; jq only where a script
#                    under scripts/ requires it
#
# Usage:
#   doctor.sh               the report for this checkout on this machine
#   doctor.sh --self-test   the checks run case by case against a fixture checkout and fakes on PATH
#                           (dotnet, docker, aspire, lsof, nc, git, jq) in a temporary folder; prints
#                           "doctor self-test passed: N cases" or each failing case with its output
# POSIX sh (dash included), awk, sed and grep. Nothing outside the temporary folder is written.
set -u

here=$(cd "$(dirname "$0")" && pwd -P)
self="$here/$(basename "$0")"
root=$(dirname "$here")
home="${HOME:-}"
os=$(uname -s 2>/dev/null || echo unknown)
default_socket=/var/run/docker.sock
container_markers="/.dockerenv /run/.containerenv"
aspire_floor=13.5 # docs/development.md, Prerequisites

needed=0
optional=0
say() { printf '%-8s %s\n' "$1" "$2"; }
lack() { # lack NEED TEXT: a missing line; NEED is 1 when a first run needs it (the exit code says so)
  if [ "$1" = 1 ]; then needed=$((needed + 1)); else optional=$((optional + 1)); fi
  say missing "$2"
}
rel() { printf '%s\n' "${1#"$root"/}"; }

# The string value of KEY inside global.json's "sdk" object, or nothing.
sdk_field() {
  [ -f "$root/global.json" ] || return 0
  awk -v key="\"$1\"" '
    /"sdk"[ \t]*:/ { insdk = 1 }
    insdk && (i = index($0, key)) {
      s = substr($0, i + length(key))
      if (sub(/^[ \t]*:[ \t]*"/, "", s)) { sub(/".*/, "", s); print s; exit }
    }
    insdk && /}/ { insdk = 0 }
  ' "$root/global.json"
}

# ver_parse VERSION: sets vM, vm and vp, and vkey, a number that sorts versions (a pre-release below its
# release); fails on anything but N.N.N with an optional -suffix.
ver_parse() {
  pv="$1" pre=0
  case "$pv" in *-*) pre=1 pv="${pv%%-*}" ;; esac
  case "$pv" in
    *[!0-9.]* | .* | *. | *..* | *.*.*.*) return 1 ;;
    *.*.*) ;;
    *) return 1 ;;
  esac
  vM="${pv%%.*}" pv="${pv#*.}"
  vm="${pv%%.*}" vp="${pv#*.}"
  vkey=$(((((vM * 1000) + vm) * 1000 + vp) * 2 + 1 - pre))
}

# sdk_accepts HAVE WANT POLICY: whether global.json's WANT under rollForward POLICY takes SDK HAVE.
sdk_accepts() {
  ver_parse "$2" || return 1
  wM=$vM wm=$vm wp=$vp wk=$vkey
  ver_parse "$1" || return 1
  [ "$vkey" -ge "$wk" ] || return 1
  case "$3" in
    disable) [ "$1" = "$2" ] ;;
    patch | latestPatch) [ "$vM" -eq "$wM" ] && [ "$vm" -eq "$wm" ] && [ $((vp / 100)) -eq $((wp / 100)) ] ;;
    feature | latestFeature) [ "$vM" -eq "$wM" ] && [ "$vm" -eq "$wm" ] ;;
    minor | latestMinor) [ "$vM" -eq "$wM" ] ;;
    major | latestMajor) true ;;
    *) return 1 ;;
  esac
}

# sdk_band WANT POLICY: the SDKs global.json accepts, in words.
sdk_band() {
  bM=${1%%.*} br=${1#*.}
  bm=${br%%.*} bp=${br#*.}
  bp=${bp%%-*}
  case "$2" in
    disable) echo "exactly $1" ;;
    patch | latestPatch) echo "$1 or a later patch of $bM.$bm.$((bp / 100))xx" ;;
    feature | latestFeature) echo "$1 or later within $bM.$bm" ;;
    minor | latestMinor) echo "$1 or later within $bM" ;;
    *) echo "$1 or later" ;;
  esac
}

# Sets docker to the CLI scripts/dotnet.sh would run, or "" when there is none.
find_docker() {
  docker="${DOCKER_BIN:-docker}"
  command -v "$docker" >/dev/null 2>&1 && return 0
  if [ -n "$home" ] && [ -x "$home/.docker/bin/docker" ]; then docker="$home/.docker/bin/docker"; else docker=""; fi
}

# The image scripts/dotnet.sh runs: its <PREFIX>_SDK_IMAGE variable when set, else the default it names.
sdk_image() {
  [ -f "$root/scripts/dotnet.sh" ] || return 0
  found=$(sed -n 's/.*\${\([A-Z0-9_]*SDK_IMAGE\):-\([^}]*\)}.*/\1 \2/p' "$root/scripts/dotnet.sh" | head -n 1)
  [ -n "$found" ] || return 0
  var=${found%% *}
  eval "value=\${$var:-}"
  echo "${value:-${found#* }}"
}

# The AppHost's launch settings (the first tools/*.AppHost), or nothing.
launch_settings() {
  for f in "$root"/tools/*.AppHost/Properties/launchSettings.json; do
    if [ -f "$f" ]; then
      echo "$f"
      return 0
    fi
  done
}

# ports_of FILE: "PORT KEY" for every port on this machine (localhost, a *.localhost name, a loopback
# or wildcard address) in an applicationUrl or a *_URL / *_URLS value, each port once, in the file's
# order.
ports_of() {
  awk '
    {
      rest = $0
      while (match(rest, /"[A-Za-z0-9_]+"[ \t]*:[ \t]*"[^"]*"/)) {
        pair = substr(rest, RSTART, RLENGTH)
        rest = substr(rest, RSTART + RLENGTH)
        key = pair; sub(/^"/, "", key); sub(/".*/, "", key)
        if (key != "applicationUrl" && key !~ /_URLS?$/) continue
        value = pair; sub(/^"[^"]*"[ \t]*:[ \t]*"/, "", value); sub(/"$/, "", value)
        n = split(value, urls, ";")
        for (i = 1; i <= n; i++) {
          if (!match(urls[i], /:\/\/[^\/]*:[0-9]+/)) continue
          hostport = substr(urls[i], RSTART + 3, RLENGTH - 3)
          port = hostport; sub(/.*:/, "", port)
          host = hostport; sub(/:[0-9]+$/, "", host)
          if (host != "localhost" && host !~ /\.localhost$/ && host != "127.0.0.1" && host != "[::1]" && host != "0.0.0.0" && host != "[::]" && host != "*" && host != "+") continue
          if (!(port in seen)) { seen[port] = 1; print port " " key }
        }
      }
    }
  ' "$1"
}

# port_holder PORT: prints who listens on PORT ("" when nobody); fails when there is nothing to ask.
port_holder() {
  asked=0
  if command -v lsof >/dev/null 2>&1; then
    asked=1
    who=$(lsof -nP -iTCP:"$1" -sTCP:LISTEN 2>/dev/null | awk 'NR == 2 { print $1 " (pid " $2 ")" }')
    if [ -n "$who" ]; then
      echo "$who"
      return 0
    fi
  fi
  # lsof lists only this user's processes; a connection finds everyone's.
  if command -v nc >/dev/null 2>&1; then
    if nc -z -w 1 127.0.0.1 "$1" >/dev/null 2>&1; then echo "a process"; fi
    return 0
  fi
  if command -v bash >/dev/null 2>&1; then
    if bash -c 'exec 3<>"/dev/tcp/127.0.0.1/$1"' _ "$1" >/dev/null 2>&1; then echo "a process"; fi
    return 0
  fi
  [ "$asked" = 1 ]
}

run_checks() {
  needed=0
  optional=0

  # --- what the later lines depend on: a local SDK, the engine, the image ---
  want=$(sdk_field version)
  policy=$(sdk_field rollForward)
  [ -n "$want" ] && [ -z "$policy" ] && policy=patch # the SDK's default once a version is pinned
  sdk="" have="" best=0
  if command -v dotnet >/dev/null 2>&1; then
    for s in $(dotnet --list-sdks 2>/dev/null | awk '{ print $1 }'); do
      have="${have:+$have, }$s"
      if [ -z "$want" ] || sdk_accepts "$s" "$want" "$policy"; then
        ver_parse "$s" || continue
        if [ "$vkey" -gt "$best" ]; then best=$vkey sdk=$s; fi
      fi
    done
  fi
  find_docker
  engine=0 server=""
  # With --format, docker info exits 0 and prints an empty version when the engine does not answer.
  [ -n "$docker" ] && server=$("$docker" info --format '{{.ServerVersion}}' 2>/dev/null) && [ -n "$server" ] && engine=1
  image=$(sdk_image)
  image_here=0
  [ "$engine" = 1 ] && [ -n "$image" ] && "$docker" image inspect "$image" >/dev/null 2>&1 && image_here=1
  container=0 # 1 when the build goes through ./scripts/dotnet.sh: no SDK here that global.json takes
  [ -z "$sdk" ] && container=1

  # --- .NET SDK ---
  if [ -n "$sdk" ]; then
    say ok ".NET SDK $sdk on PATH${want:+, which global.json accepts ($want, rollForward $policy)}"
  else
    if [ -z "$have" ]; then
      found="PATH has no dotnet"
      if [ -n "$home" ] && [ -x "$home/.dotnet/dotnet" ]; then found="$found (~/.dotnet/dotnet exists: put ~/.dotnet on PATH)"; fi
    else
      found="PATH has $have"
    fi
    if [ -n "$want" ]; then
      asks="global.json asks for $(sdk_band "$want" "$policy")"
      get="https://dotnet.microsoft.com/download/dotnet/${want%.*}"
    else
      asks="global.json pins no version"
      get="https://dotnet.microsoft.com/download/dotnet"
    fi
    if [ "$image_here" = 1 ]; then
      lack 0 ".NET SDK: optional while the SDK image is here, since ./scripts/dotnet.sh builds and tests without one; aspire run and the dev certificate need one: $asks, $found ($get)"
    else
      lack 1 ".NET SDK: $asks, $found; install one ($get), or build the SDK image (below) and work through ./scripts/dotnet.sh"
    fi
  fi

  # --- Docker ---
  if [ -z "$docker" ]; then
    lack 1 "Docker: no docker CLI on PATH (or DOCKER_BIN); PostgreSQL, the integration tests and the SDK container need an engine (docs/development.md, Prerequisites)"
  elif [ "$engine" = 0 ]; then
    lack 1 "Docker: the engine does not answer ($docker info); start Docker Desktop or your engine${DOCKER_HOST:+, or check DOCKER_HOST=$DOCKER_HOST}"
  else
    say ok "Docker: the engine answers (server $server)"
  fi

  # --- the SDK image ---
  if [ -z "$image" ]; then
    say skip "SDK image: scripts/dotnet.sh names none"
  elif [ "$engine" = 0 ]; then
    say skip "SDK image $image: Docker does not answer"
  elif [ "$image_here" = 1 ]; then
    say ok "SDK image $image, which ./scripts/dotnet.sh runs"
  elif [ "$container" = 1 ]; then
    lack 1 "SDK image $image: build it once with: docker build -t $image scripts/sdk-image"
  else
    lack 0 "SDK image $image: optional with a local SDK (./scripts/dotnet.sh and the dev container use it); build it with: docker build -t $image scripts/sdk-image"
  fi

  # --- Aspire CLI ---
  settings=$(launch_settings)
  apphost="tools/*.AppHost"
  [ -n "$settings" ] && apphost=$(rel "${settings%/Properties/launchSettings.json}")
  without="without it, dotnet run --project $apphost starts the same app"
  if ! command -v aspire >/dev/null 2>&1; then
    lack 0 "Aspire CLI: optional; aspire run and the aspire MCP server need $aspire_floor or later (dotnet tool install -g Aspire.Cli); $without"
  else
    av=$(aspire --version 2>/dev/null | sed -n 's/^[^0-9]*\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/p' | head -n 1)
    ver_parse "$aspire_floor.0"
    floor=$vkey
    if [ -z "$av" ] || ! ver_parse "$av"; then
      say skip "Aspire CLI: aspire --version printed no version"
    elif [ "$vkey" -ge "$floor" ]; then
      say ok "Aspire CLI $av (aspire run, and the aspire MCP server in .mcp.json)"
    else
      lack 0 "Aspire CLI $av: optional, but aspire run and the aspire MCP server need $aspire_floor or later (dotnet tool update -g Aspire.Cli); $without"
    fi
  fi

  # --- the development certificate ---
  if [ -z "$sdk" ]; then
    say skip "dev certificate: dotnet dev-certs needs a local .NET SDK"
  elif (cd "$root" && dotnet dev-certs https --check --trust) >/dev/null 2>&1; then
    say ok "dev certificate trusted (dotnet dev-certs https --check --trust)"
  else
    lack 0 "dev certificate: not trusted; optional, the AppHost's https profile needs it and its http profile does without: dotnet dev-certs https --trust"
  fi

  # --- the AppHost's ports ---
  if [ -z "$settings" ]; then
    say skip "AppHost ports: no tools/*.AppHost/Properties/launchSettings.json"
  else
    ports=$(ports_of "$settings")
    if [ -z "$ports" ]; then
      say skip "AppHost ports: $(rel "$settings") names no localhost port"
    else
      free="" busy="" blind=0
      # One "PORT KEY" per line; a here-document keeps the loop in this shell, and its variables with it.
      while read -r port name; do
        if who=$(port_holder "$port"); then
          if [ -n "$who" ]; then busy="${busy:+$busy, }$port ($name) held by $who"; else free="${free:+$free }$port"; fi
        else
          blind=1
        fi
      done <<EOF
$ports
EOF
      if [ "$blind" = 1 ]; then
        say skip "AppHost ports: no lsof, nc or bash to check $(echo "$ports" | awk '{ printf "%s%s", (NR > 1 ? " " : ""), $1 }') with"
      elif [ -n "$busy" ]; then
        lack 1 "AppHost ports in use: $busy; stop what holds them (an AppHost already running?) or change $(rel "$settings"); doctor frees nothing"
      else
        say ok "AppHost ports free: $free ($(rel "$settings"))"
      fi
    fi
  fi

  # --- Testcontainers: the engine endpoint the tests get ---
  inside=0
  for marker in $container_markers; do [ -e "$marker" ] && inside=1; done
  case "${DOCKER_HOST:-}" in
    tcp://127.* | tcp://localhost* | tcp://\[::1\]* | tcp://0.0.0.0*)
      lack "$container" "Testcontainers: DOCKER_HOST=$DOCKER_HOST is not reachable from a container, so the tests ./scripts/dotnet.sh runs cannot reach the engine; use an address a container reaches (docs/development.md, \"Docker engines other than Docker Desktop\")"
      ;;
    tcp://*)
      say ok "Testcontainers: DOCKER_HOST=$DOCKER_HOST, passed through; published ports at ${TESTCONTAINERS_HOST_OVERRIDE:-the DOCKER_HOST address}"
      ;;
    "" | unix://*)
      socket="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-}"
      if [ -n "$socket" ]; then
        where="$socket, the socket as the engine sees it (TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE)"
      elif [ "$os" = Darwin ]; then
        socket=$default_socket
        where="$socket, the engine VM's socket"
      else
        socket="${DOCKER_HOST:-}"
        socket="${socket#unix://}"
        socket="${socket:-$default_socket}"
        where=$socket
      fi
      if [ "$inside" = 1 ] && [ -z "${TESTCONTAINERS_HOST_OVERRIDE:-}" ]; then
        lack 0 "Testcontainers: inside a container, the tests reach the containers they start through the host: set TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal, as the dev container does"
      elif [ -z "${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-}" ] && [ "$os" != Darwin ] && [ ! -e "$socket" ]; then
        lack "$container" "Testcontainers: ./scripts/dotnet.sh would mount $socket, which does not exist; point DOCKER_HOST=unix://<path> at your engine's socket (docs/development.md, \"Docker engines other than Docker Desktop\")"
      else
        say ok "Testcontainers: the engine at $where; published ports at ${TESTCONTAINERS_HOST_OVERRIDE:-host.docker.internal}"
      fi
      ;;
    *)
      lack "$container" "Testcontainers: DOCKER_HOST=$DOCKER_HOST; ./scripts/dotnet.sh takes unix:// and tcp:// only (docs/development.md, \"Docker engines other than Docker Desktop\")"
      ;;
  esac

  # --- git, and jq where a script needs it ---
  if command -v git >/dev/null 2>&1; then
    say ok "$(git --version 2>/dev/null | head -n 1)"
  else
    lack 0 "git: optional for a first run; the docs-drift and ADR checks, the commit hook and some hook self-test cases need it"
  fi
  wants_jq=""
  for f in "$root"/scripts/*.sh; do
    [ -f "$f" ] && [ "$f" != "$self" ] || continue
    if grep -q 'command -v jq' "$f"; then wants_jq="${wants_jq:+$wants_jq, }$(rel "$f")"; fi
  done
  if [ -n "$wants_jq" ]; then
    if command -v jq >/dev/null 2>&1; then
      say ok "jq $(jq --version 2>/dev/null | sed 's/^jq-//'), which $wants_jq needs"
    else
      lack 0 "jq: optional for a first run; $wants_jq needs it"
    fi
  fi

  if [ "$needed" -gt 0 ]; then
    echo "doctor: $needed needed and $optional optional missing (above); fix the needed ones before a first run."
    return 1
  fi
  if [ "$optional" -gt 0 ]; then
    echo "doctor: ready for a first run; $optional optional missing (above)."
  else
    echo "doctor: ready for a first run."
  fi
}

self_test() {
  work=$(mktemp -d) || {
    echo "doctor self-test: cannot create a temporary folder"
    exit 1
  }
  trap 'rm -rf "$work"' EXIT
  trap 'exit 130' INT TERM
  bin="$work/bin" sys="$work/sys" state="$work/state" repo="$work/repo" out="$work/out"
  mkdir -p "$bin" "$sys" "$state" "$repo/scripts" "$repo/tools/Acme.AppHost/Properties"

  # The utilities the checks and the fakes use, and nothing else: a real dotnet, docker or lsof on
  # this machine never answers in a fake's place.
  for tool in awk sed grep head cat; do
    found=$(command -v "$tool") || {
      echo "doctor self-test: $tool is not on PATH"
      exit 1
    }
    ln -s "$found" "$sys/$tool"
  done

  # --- the fixture checkout: global.json, a scripts/dotnet.sh naming its image, the AppHost's ports ---
  global() { # global VERSION POLICY
    printf '{\n  "sdk": {\n    "version": "%s",\n    "rollForward": "%s"\n  },\n  "test": { "runner": "Microsoft.Testing.Platform" }\n}\n' "$1" "$2" > "$repo/global.json"
  }
  printf '#!/bin/sh\n# build it once with:  docker build -t acme-sdk:10.0 scripts/sdk-image\nexec docker run --rm \\\n  "${ACME_SDK_IMAGE:-acme-sdk:10.0}" \\\n  dotnet "$@"\n' > "$repo/scripts/dotnet.sh"
  # This script too: its own jq probe must not count as a script that needs jq.
  cat "$self" > "$repo/scripts/doctor.sh"
  settings="$repo/tools/Acme.AppHost/Properties/launchSettings.json"
  cat > "$settings" <<'JSON'
{
  "profiles": {
    "https": {
      "applicationUrl": "https://localhost:41001;http://localhost:41002",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": "http://localhost:41003;https://127.0.0.1:41004",
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:41005",
        "REMOTE_COLLECTOR_URL": "https://collector.example.com:41999"
      }
    },
    "http": {
      "applicationUrl": "http://localhost:41002", "environmentVariables": { "ASPIRE_DASHBOARD_MCP_ENDPOINT_URL": "http://acme.dev.localhost:41006" }
    }
  }
}
JSON

  # --- the fakes, each answering from a file under $state ---
  fake() { # fake NAME BODY: an executable NAME in $bin that runs BODY
    printf '#!/bin/sh\n%s\n' "$2" > "$bin/$1"
    chmod +x "$bin/$1"
  }
  fakes() {
    fake dotnet "case \"\$1\" in
  --list-sdks) cat '$state/sdks' ;;
  dev-certs) exit \"\$(cat '$state/certs')\" ;;
  *) exit 2 ;;
esac"
    fake docker "case \"\$1\" in
  info) case \"\$(cat '$state/engine')\" in
      up) echo 28.0.1 ;;
      quiet) echo ;; # what docker info --format prints, exiting 0, when the engine does not answer
      *) echo 'Cannot connect to the Docker daemon' >&2; exit 1 ;;
    esac ;;
  image) grep -qxF \"\$3\" '$state/images' ;;
  *) exit 2 ;;
esac"
    fake aspire "[ \"\$1\" = --version ] && cat '$state/aspire'"
    fake lsof "for a; do case \"\$a\" in -iTCP:*) port=\"\${a#-iTCP:}\" ;; esac; done
line=\$(grep \"^\$port \" '$state/listening') || exit 1
set -- \$line
echo 'COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME'
echo \"\$2 \$3 me 3u IPv4 0x1 0t0 TCP *:\$1 (LISTEN)\""
    # A connection also finds the ports in $state/hidden: another user's, which lsof does not list.
    fake nc "for a; do port=\$a; done
cat '$state/listening' '$state/hidden' | grep -q \"^\$port \""
    fake bash "for a; do port=\$a; done
cat '$state/listening' '$state/hidden' | grep -q \"^\$port \""
    fake git "echo 'git version 2.47.1'"
    fake jq "echo 'jq-1.7.1'"
  }
  reset() { # every case starts from a Linux machine with everything in place
    rm -rf "$bin" "$work/home" "$work/dockerenv"
    mkdir -p "$bin" "$work/home"
    fakes
    echo '10.0.403 [/usr/share/dotnet/sdk]' > "$state/sdks"
    echo 0 > "$state/certs"
    echo up > "$state/engine"
    echo 'acme-sdk:10.0' > "$state/images"
    echo '13.6.0+0123abcd' > "$state/aspire"
    : > "$state/listening"
    : > "$state/hidden"
    : > "$work/docker.sock"
    global 10.0.403 latestFeature
    printf '#!/bin/sh\ncommand -v jq >/dev/null || exit 1\n' > "$repo/scripts/needs-jq.sh"
    case_os=Linux
  }

  # run [NAME=VALUE...]: the checks against the fixture and the fakes, with those variables exported
  # and every variable the checks read from the environment otherwise unset.
  run() {
    (
      unset DOCKER_HOST DOCKER_BIN TESTCONTAINERS_HOST_OVERRIDE TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE ACME_SDK_IMAGE
      for assignment; do export "$assignment"; done
      root="$repo" self="$repo/scripts/doctor.sh" home="$work/home" os="$case_os"
      default_socket="$work/docker.sock" container_markers="$work/dockerenv"
      PATH="$bin:$sys"
      run_checks
    ) > "$out" 2>&1
    code=$?
  }

  cases=0
  failed=0
  label=""
  flunk() {
    failed=$((failed + 1))
    printf 'FAIL %s: %s\n' "$label" "$1"
    sed 's/^/  | /' "$out"
  }
  exits() { # exits CODE: the last run's exit status
    cases=$((cases + 1))
    [ "$code" = "$1" ] || flunk "expected exit $1, got $code"
  }
  line() { # line STATUS TEXT: the last run printed a line that starts with STATUS and holds TEXT
    cases=$((cases + 1))
    grep -F -- "$2" "$out" | grep -q "^$1 " || flunk "expected a '$1' line holding '$2'"
  }
  never() { # never TEXT: no line of the last run holds TEXT
    cases=$((cases + 1))
    if grep -qF -- "$1" "$out"; then flunk "expected no line holding '$1'"; fi
  }

  label="everything in place"
  reset
  run
  exits 0
  line ok ".NET SDK 10.0.403 on PATH, which global.json accepts (10.0.403, rollForward latestFeature)"
  line ok "Docker: the engine answers (server 28.0.1)"
  line ok "SDK image acme-sdk:10.0, which ./scripts/dotnet.sh runs"
  line ok "Aspire CLI 13.6.0"
  line ok "dev certificate trusted"
  line ok "AppHost ports free: 41001 41002 41003 41004 41005 41006 (tools/Acme.AppHost/Properties/launchSettings.json)"
  never 41999
  line ok "Testcontainers: the engine at $work/docker.sock; published ports at host.docker.internal"
  line ok "git version 2.47.1"
  line ok "jq 1.7.1, which scripts/needs-jq.sh needs"
  never "scripts/doctor.sh"
  line doctor: "ready for a first run."

  label="a later feature band under latestFeature"
  reset
  printf '10.0.100 [/sdk]\n10.0.500 [/sdk]\n' > "$state/sdks"
  run
  exits 0
  line ok ".NET SDK 10.0.500 on PATH"

  label="an earlier patch, another minor, another major and a pre-release of the pinned version"
  reset
  printf '10.0.402 [/sdk]\n10.1.100 [/sdk]\n11.0.100 [/sdk]\n10.0.403-rc.1 [/sdk]\n' > "$state/sdks"
  run
  exits 0
  line missing ".NET SDK: optional while the SDK image is here, since ./scripts/dotnet.sh builds and tests without one"
  line missing "global.json asks for 10.0.403 or later within 10.0, PATH has 10.0.402, 10.1.100, 11.0.100, 10.0.403-rc.1"
  line skip "dev certificate: dotnet dev-certs needs a local .NET SDK"
  line doctor: "ready for a first run; 1 optional missing"

  label="rollForward patch refuses a later feature band"
  reset
  global 10.0.403 patch
  echo '10.0.500 [/sdk]' > "$state/sdks"
  run
  line missing "global.json asks for 10.0.403 or a later patch of 10.0.4xx"

  label="rollForward disable takes the pinned version only"
  reset
  global 10.0.403 disable
  echo '10.0.404 [/sdk]' > "$state/sdks"
  run
  line missing "global.json asks for exactly 10.0.403"
  echo '10.0.403 [/sdk]' > "$state/sdks"
  run
  line ok ".NET SDK 10.0.403 on PATH"

  label="no SDK on PATH, one in ~/.dotnet, and the SDK image here"
  reset
  mkdir -p "$work/home/.dotnet"
  mv "$bin/dotnet" "$work/home/.dotnet/dotnet"
  run
  exits 0
  line missing "./scripts/dotnet.sh builds and tests without one"
  line missing "PATH has no dotnet (~/.dotnet/dotnet exists: put ~/.dotnet on PATH)"

  label="no SDK and no SDK image"
  reset
  rm "$bin/dotnet"
  : > "$state/images"
  run
  exits 1
  line missing ".NET SDK: global.json asks for 10.0.403 or later within 10.0, PATH has no dotnet; install one (https://dotnet.microsoft.com/download/dotnet/10.0)"
  line missing "SDK image acme-sdk:10.0: build it once with: docker build -t acme-sdk:10.0 scripts/sdk-image"
  line doctor: "2 needed and 0 optional missing"

  label="no SDK image, with a local SDK"
  reset
  : > "$state/images"
  run
  exits 0
  line missing "SDK image acme-sdk:10.0: optional with a local SDK"

  label="the image override variable scripts/dotnet.sh names"
  reset
  echo 'other-sdk:1' > "$state/images"
  run ACME_SDK_IMAGE=other-sdk:1
  line ok "SDK image other-sdk:1"

  label="an engine that does not answer"
  reset
  echo down > "$state/engine"
  run
  exits 1
  line missing "Docker: the engine does not answer"
  line skip "SDK image acme-sdk:10.0: Docker does not answer"

  label="an engine that does not answer, which docker info --format reports with exit 0"
  reset
  echo quiet > "$state/engine"
  run
  exits 1
  line missing "Docker: the engine does not answer"
  never "the engine answers"

  label="the docker CLI only in ~/.docker/bin, where Docker Desktop leaves it"
  reset
  mkdir -p "$work/home/.docker/bin"
  mv "$bin/docker" "$work/home/.docker/bin/docker"
  run
  exits 0
  line ok "Docker: the engine answers"

  label="no docker CLI at all"
  reset
  rm "$bin/docker"
  run DOCKER_BIN=podman
  exits 1
  line missing "Docker: no docker CLI on PATH (or DOCKER_BIN)"

  label="an Aspire CLI older than $aspire_floor"
  reset
  echo '13.4.2+feed' > "$state/aspire"
  run
  exits 0
  line missing "Aspire CLI 13.4.2: optional, but aspire run and the aspire MCP server need $aspire_floor or later"

  label="no Aspire CLI"
  reset
  rm "$bin/aspire"
  run
  exits 0
  line missing "without it, dotnet run --project tools/Acme.AppHost starts the same app"

  label="an untrusted development certificate"
  reset
  echo 6 > "$state/certs"
  run
  exits 0
  line missing "dev certificate: not trusted; optional"

  label="a port in use, named by lsof"
  reset
  echo '41003 dotnet 4242' > "$state/listening"
  run
  exits 1
  line missing "AppHost ports in use: 41003 (ASPNETCORE_URLS) held by dotnet (pid 4242); stop what holds them"

  label="a port in use, found by nc alone"
  reset
  rm "$bin/lsof"
  echo '41005 - -' > "$state/listening"
  run
  exits 1
  line missing "41005 (DOTNET_DASHBOARD_OTLP_ENDPOINT_URL) held by a process"

  label="another user's process on a port, which lsof does not list and a connection finds"
  reset
  echo '41001 - -' > "$state/hidden"
  run
  exits 1
  line missing "41001 (applicationUrl) held by a process"

  label="a port in use, found by bash's /dev/tcp alone"
  reset
  rm "$bin/lsof" "$bin/nc"
  echo '41006 - -' > "$state/listening"
  run
  exits 1
  line missing "41006 (ASPIRE_DASHBOARD_MCP_ENDPOINT_URL) held by a process"

  label="lsof alone"
  reset
  rm "$bin/nc" "$bin/bash"
  echo '41002 dotnet 77' > "$state/listening"
  run
  exits 1
  line missing "41002 (applicationUrl) held by dotnet (pid 77)"

  label="neither lsof, nc nor bash"
  reset
  rm "$bin/lsof" "$bin/nc" "$bin/bash"
  run
  exits 0
  line skip "AppHost ports: no lsof, nc or bash to check 41001 41002 41003 41004 41005 41006 with"

  label="no AppHost launch settings, and no Aspire CLI"
  reset
  mv "$settings" "$work/launchSettings.json"
  rm "$bin/aspire"
  run
  line skip "AppHost ports: no tools/*.AppHost/Properties/launchSettings.json"
  line missing "dotnet run --project tools/*.AppHost starts the same app"
  mv "$work/launchSettings.json" "$settings"

  label="DOCKER_HOST on a loopback TCP address, without a local SDK"
  reset
  rm "$bin/dotnet"
  run DOCKER_HOST=tcp://127.0.0.1:2375
  exits 1
  line missing "Testcontainers: DOCKER_HOST=tcp://127.0.0.1:2375 is not reachable from a container"

  label="DOCKER_HOST on a loopback TCP address, with a local SDK"
  reset
  run DOCKER_HOST=tcp://localhost:2375
  exits 0
  line missing "is not reachable from a container"

  label="DOCKER_HOST on another TCP address"
  reset
  run DOCKER_HOST=tcp://10.0.0.5:2375
  line ok "Testcontainers: DOCKER_HOST=tcp://10.0.0.5:2375, passed through; published ports at the DOCKER_HOST address"

  label="a DOCKER_HOST scheme scripts/dotnet.sh refuses"
  reset
  rm "$bin/dotnet"
  run DOCKER_HOST=ssh://me@build-box
  exits 1
  line missing "./scripts/dotnet.sh takes unix:// and tcp:// only"

  label="a Linux socket that does not exist"
  reset
  rm "$work/docker.sock" "$bin/dotnet"
  run
  exits 1
  line missing "./scripts/dotnet.sh would mount $work/docker.sock, which does not exist"
  run DOCKER_HOST="unix://$work/rootless.sock"
  line missing "would mount $work/rootless.sock"

  label="macOS mounts the engine VM's socket"
  reset
  rm "$work/docker.sock"
  case_os=Darwin
  run
  exits 0
  line ok "Testcontainers: the engine at $work/docker.sock, the engine VM's socket"

  label="a socket override names the engine's own path"
  reset
  run TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/run/podman/podman.sock
  line ok "the engine at /run/podman/podman.sock, the socket as the engine sees it"

  label="inside a container"
  reset
  : > "$work/dockerenv"
  run
  exits 0
  line missing "set TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal, as the dev container does"
  run TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal
  line ok "published ports at host.docker.internal"

  label="no git, and no script that needs jq"
  reset
  rm "$bin/git" "$repo/scripts/needs-jq.sh"
  run
  exits 0
  line missing "git: optional for a first run"
  never jq

  label="no jq while a script needs it"
  reset
  rm "$bin/jq"
  run
  exits 0
  line missing "jq: optional for a first run; scripts/needs-jq.sh needs it"

  if [ "$failed" -ne 0 ]; then
    echo "doctor self-test failed: $failed of $cases cases."
    exit 1
  fi
  echo "doctor self-test passed: $cases cases"
}

case "${1:-}" in
  "")
    run_checks
    exit
    ;;
  --self-test) self_test ;;
  *)
    echo "usage: scripts/doctor.sh [--self-test]" >&2
    exit 2
    ;;
esac

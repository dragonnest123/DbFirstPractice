#!/usr/bin/env bash
set -euo pipefail

export MSYS2_ARG_CONV_EXCL="*"
export MSYS_NO_PATHCONV=1

export PROGRAMFILES="${PROGRAMFILES:-C:\Program Files}"
export PROGRAMW6432="${PROGRAMW6432:-C:\Program Files}"
export PROGRAMDATA="${PROGRAMDATA:-C:\ProgramData}"
export ALLUSERSPROFILE="${ALLUSERSPROFILE:-C:\ProgramData}"
export SYSTEMDRIVE="${SYSTEMDRIVE:-C:}"
export SYSTEMROOT="${SYSTEMROOT:-C:\Windows}"
export WINDIR="${WINDIR:-C:\Windows}"
export OS="${OS:-Windows_NT}"
export USERPROFILE="${USERPROFILE:-${HOMEDRIVE:-C:}${HOMEPATH:-}}"

gateway_port="${COURSE_GATEWAY_PORT:-8080}"
for argument in "$@"; do
  if [[ "$argument" == "config" ]]; then
    gateway_port=8080
    break
  fi
done
export COURSE_GATEWAY_PORT="$gateway_port"

normalize_args=()
for argument in "$@"; do
  case "$argument" in
    [A-Za-z]:\\*)
      normalize_args+=("${argument//\\//}")
      ;;
    *)
      normalize_args+=("$argument")
      ;;
  esac
done

run_build() {
  local -a prefix=() flags=() services=()
  local mode=prefix
  for argument in "${normalize_args[@]}"; do
    case "$mode" in
      prefix)
        if [[ "$argument" == "build" ]]; then
          mode=flags
        else
          prefix+=("$argument")
        fi
        ;;
      flags)
        if [[ "$argument" == -* ]]; then
          flags+=("$argument")
        else
          services+=("$argument")
          mode=services
        fi
        ;;
      services)
        services+=("$argument")
        ;;
    esac
  done

  local has_a=false has_b=false
  for service in "${services[@]}"; do
    [[ "$service" == "worker-a" ]] && has_a=true
    [[ "$service" == "worker-b" ]] && has_b=true
  done

  if [[ "$has_a" == true && "$has_b" == true ]]; then
    local -a first=() second=(worker-b)
    for service in "${services[@]}"; do
      [[ "$service" != "worker-b" ]] && first+=("$service")
    done
    docker compose "${prefix[@]}" build "${flags[@]}" "${first[@]}"
    docker compose "${prefix[@]}" build "${flags[@]}" "${second[@]}"
    return
  fi

  docker compose "${normalize_args[@]}"
}

run_build
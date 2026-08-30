/**
 * Client-Version der Angular-App.
 *
 * Default '0.0.0-local' (lokale Dev-Builds). Im Docker-Build wird dieser
 * Wert vom Release-Tag überschrieben (siehe Dockerfile: ARG VERSION + sed).
 *
 * Zweck: Das Version-Badge auf Terminal-/Admin-Seite zeigt Client- und
 * Server-Version nebeneinander. Weichen sie ab, wurde (fast sicher) eine
 * alte App aus dem Browser-Cache geladen — das Badge markiert den Fall.
 */
export const APP_VERSION = '0.0.0-local';

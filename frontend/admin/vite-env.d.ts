/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_ENTRA_TENANT_ID: string
  readonly VITE_ENTRA_CLIENT_ID: string
  readonly VITE_API_SCOPE: string
  readonly VITE_API_BASE: string
  readonly VITE_ENVIRONMENT?: string
  readonly VITE_PLATFORM_VERSION?: string
  readonly VITE_CUSTOMER_NAME?: string
  // Control-plane admin role/group — matches the gateway's AdminAuth config.
  readonly VITE_ADMIN_ROLE?: string
  readonly VITE_ADMIN_GROUP_ID?: string
}
interface ImportMeta {
  readonly env: ImportMetaEnv
}

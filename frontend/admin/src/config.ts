export const env = {
  environment: (import.meta.env.VITE_ENVIRONMENT ?? 'DEV').toUpperCase(),
  version: import.meta.env.VITE_PLATFORM_VERSION ?? '1.0.0',
  customer: import.meta.env.VITE_CUSTOMER_NAME ?? 'AgentPermit',
}

export const isProd = env.environment === 'PROD'

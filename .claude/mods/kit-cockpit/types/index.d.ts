export type Cockpit = {
  agents: string[]
  blocked: number
  overridden: number
}

declare module 'claude-code' {
  interface PluginState {
    'kit-cockpit': { view: Cockpit }
  }
}

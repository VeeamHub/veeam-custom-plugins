/**
 * Copyright © Veeam Software Group GmbH.
 */

export const getMyPluginPrefix = (url: string): string => `/plugins/${process.env.PLUGIN_ID}/api/v1${url}`;

/**
 * Copyright © Veeam Software Group GmbH.
 */

import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';
import { webpackBaseConfig } from '@veeam-vspc/configs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const packageJson = JSON.parse(fs.readFileSync(path.join(__dirname, './package.json'), 'utf8'));
const productModulesForBabel = [
    path.resolve(__dirname, 'node_modules/@veeam-vspc/shared'),
];
const pluginId = packageJson.config.pluginId;
const publicUrl = `/plugins/${pluginId}/`;

export default webpackBaseConfig({
    entry: {
        main: ['./main'],
    },
    outputPath: `build/ui-content`,
    envVars: {
        VERSION: packageJson.version,
        PUBLIC_URL: publicUrl,
        PLUGIN_ID: pluginId,
    },
    customRulesConfig: {
        scriptIncludePaths: productModulesForBabel,
    },
    publicPath: {
        prod: '',
        dev: '/',
    },
    terserCustomOptions: {
        // Creation license file for main.js in production build
        extractComments: defaultPattern => `${defaultPattern}|Copyright © ${'Veeam Software'} Group GmbH`,
    },
    extraWebpackPluginOptions: {
        htmlWebpackPluginConfig: {
            favicon:  path.resolve(__dirname, 'public/favicon.ico'),
            template: path.join(__dirname, 'node_modules/@veeam-vspc/shared/public/index.html'),
        },
        cspPluginConfig: {
            directiveSet: {
                'default-src': [`'self'`],
                'connect-src': [
                    `'self'`,
                    `https://rest-ai.veeam.com:443`,
                    `wss://rest-ai.veeam.com:443`,
                    `https://rest-ai.staging.veeam.com:443`,
                    `wss://rest-ai.staging.veeam.com:443`,
                ],
                'script-src': [`'self'`],
                'style-src': [`'self'`, `'unsafe-inline'`],
                'object-src': [`'none'`],
                'base-uri': [`'self'`],
                'img-src': [`'self'`, `blob: data:`],
                'font-src': [`'self'`, `data:`],
            },
        },
    },
});


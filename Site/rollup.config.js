import { nodeResolve } from '@rollup/plugin-node-resolve';
import commonjs from '@rollup/plugin-commonjs';
import json from '@rollup/plugin-json';
import esbuild from 'rollup-plugin-esbuild';
import copy from 'rollup-plugin-copy';

const nodeVersion = parseInt(process.version.substring(1));
if (isNaN(nodeVersion) || nodeVersion < 20) {
  console.error('need node >= v20');
  process.exit(1);
}

const outPath = 'out';

export default [
  {
    input: 'src/main.ts',
    output: [
      {
        file: `${outPath}/main.js`,
        format: 'esm',
        sourcemap: true,
      },
    ],
    plugins: [
      json(),
      nodeResolve({ extensions: ['.mjs', '.js', '.json', '.node', '.ts'] }),
      esbuild({ tsconfig: './src/tsconfig.json', target: 'esnext' }),
      commonjs(),
      copy({
        hook: 'writeBundle',
        targets: [
          { src: 'index.html', dest: outPath },
          { src: 'public/css/*', dest: `${outPath}/css` },
          { src: 'public/js/*', dest: `${outPath}/js` },
          { src: 'public/favicon.ico', dest: outPath },
          { src: 'public/menu.svg', dest: outPath },
        ],
      }),
    ],
    watch: {
      clearScreen: false,
    },
  },
];

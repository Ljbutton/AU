// Bundles the plugin (src/plugin.js, the Stream Deck SDK and its WebSocket library) into the one file
// Stream Deck runs: com.ljbutton.redalert.sdPlugin/bin/plugin.js. The same setup as Elgato's own
// plugin template, without TypeScript.

import commonjs from "@rollup/plugin-commonjs";
import nodeResolve from "@rollup/plugin-node-resolve";

const sdPlugin = "com.ljbutton.redalert.sdPlugin";

export default {
	input: "src/plugin.js",
	output: {
		file: `${sdPlugin}/bin/plugin.js`,
		format: "es",
		sourcemap: false,
	},
	plugins: [
		nodeResolve({ browser: false, exportConditions: ["node"], preferBuiltins: true }),
		commonjs(),
		{
			// Stream Deck runs bin/plugin.js with Node; this makes Node read it as an ES module.
			name: "emit-module-package-file",
			generateBundle() {
				this.emitFile({ fileName: "package.json", source: `{ "type": "module" }`, type: "asset" });
			},
		},
	],
};

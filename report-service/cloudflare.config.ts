import { bindings, defineConfig, triggers } from "cf/config";

export default defineConfig({
	worker: {
		name: "shim-support-reports",
		compatibilityDate: "2026-10-27",
		entrypoint: "src/index.js",
		workersDev: false,
		observability: {
			enabled: true,
			headSamplingRate: 1,
		},
		domains: [
			"dev.amslabs.net",
		],
		triggers: [
			triggers.scheduled({
				schedule: "0 0 * * *",
			}),
		],
		env: {
			ACCESS_AUD: bindings.text("72cd66704a9e2e2840fa998d79c0e24ae51691fcac1e6e93aa1ad88cccc41d66"),
			SUPPORT_REPORTS: bindings.r2({
				name: "shim-support-reports",
			}),
			REPORT_LIMIT: bindings.rateLimit({
				namespace: "8462917315",
				simple: {
					limit: 5,
					period: 60,
				},
			}),
		},
	},
});

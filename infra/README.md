# Infra (Phase 4)

Bicep or `azd` templates for the Azure resources the project uses, so anyone can reproduce it and tear it down:

- Foundry resource + project + model deployment (mini model by default)
- (Phase 4) resources for durable execution, hosted agents, evaluations

Until then, create the Foundry project by hand and note what you did in `docs/notes/foundry-setup.md`.

Always document the teardown (`azd down`) and set a budget alert on the subscription.

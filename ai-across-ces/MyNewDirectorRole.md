My new role is director of ai to lead teams, please :

1) Interactive AI training:  Helping teams share their success stories with AI - with a "How to section", new agents, new LLMs, new SLMs, prompting, designs through an interactive training curriculum that can be extended as needed.
	a. Team will work the AI team to put together AI training programs that teams can experience real world scenario's.
    b. The interactive training will be available online internally only and will provide direction on new agent releases, teams successes etc...

2) AI Quality assurance program, focusing on Agentic readiness and provide feedback to the team with recommendations:
	a. JIRA story quality - Industry standard approaches.  Perhaps use a zero token code like Gemma 4 to grade JIRA story to see if it can effectively document all the changes.  
	b. Using a golden baseline repo to test agents effectiveness. Use this baseline and already know how the code will work after the changes.
	c. Audit repo regression testing and determine if it effectively covers the risks of enhancements to the repo.  
	d. Test the Context, industry standard approaches. Create test JIRAs that are test only, but are real changes and real test data, but not checked in.

3) Break down barriers that limit their AI usage... Provide leadership to the teams on how to best spend their token allowance, by understanding what models to use.  
	a) Introduce to the teams free models like Gemma 4.
	b) Provide direction to the teams on grounding Gemma 4
	c) Train Gemma 4 with specific domain knowledge, to decrease the token cost across teams.

4) Monitor each agents effectiveness, by reviewing a percentage of the stories and code the agents implemented and promoted to production.  We will need a base line for the following.
	a)  Use LLM in VS Code Copilot chat to use the agent the team used to update Version 1.0 of the code to version 1.1 with the change.  But this time, to not make the change only document the steps it would take to do the change and the context it used to determine the steps .
	b)  Have the LLM repeat step a and increase the context available until the quanlity of what the agent delivers in the report hits a plateau based on a standard scoring. 
	c)  Have the LLM repeat step a and decrease the context available until the quanlity of what the agent does based on a standard scoring hits a low.


5) Audit across CES repos to analyze common business logic (functional) and common Enterprise (Non-Functional) libraries.
	a) The goal is to use AI to find common functional and non-functional code and provide guidance and a strategy on consolidating the enterprise architecture.

6) Audit across CES repos for cortex at the cortex:
	a) (Tier 1 - all repos high-level what they are), 
	b) AMP ID (Tier 2 - one level lower than Cortex - department level, groups of repos deeper than cortex) 
	c) repo level (Tier 3 - specific repo level complete context artifacts).